﻿#define USE_DIRECT_MEMORY_ACCESS
#define USE_SLOW_PC

/*
 
Execution Time to render 1 video frame

Mode    Single      Side2Side       Overlay
GDI+    3.7 ms      7   ms          5.4 ms
Direct  2.6 ms      3.9 ms          3.6 ms

*/

using Accord.Audio;
using Accord.DirectSound;
using Accord.Video.DirectShow;
using Accord.Video.FFMPEG;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VACamera.Properties;

namespace VACamera
{
    public partial class FormMain : Form
    {
        enum VideoRecordState
        {
            IDLE,
            RECORDING
        }

        VideoRecordState videoRecordState = VideoRecordState.IDLE;
        VideoFileWriter videoFileWriter = null;
        Thread videoRenderThread = null;
        int recordTime = 0;
        int recordPart = 0;
        static readonly Object syncRender = new Object();

        string outputFolder = "C:\\records";
        string outputFile = "";
        string videoExtension = ".mp4";

        SessionInfo sessionInfo = new SessionInfo();
        Settings settings = new Settings();

        List<AudioDeviceInfo> audioDevices = null;
        IAudioSource audioDevice = null;

        FilterInfoCollection videoDevices;
        VideoCaptureDevice videoDevice1 = null;
        VideoCaptureDevice videoDevice2 = null;
        bool isPreviewing = true;

        Bitmap frame1 = null;
        static readonly Object syncFrame1 = new Object();

        Bitmap frame2 = null;
        static readonly Object syncFrame2 = new Object();

        Bitmap _videoFrame = null;
        Graphics _graphics = null;
        DateTime lastVideoFrameTime = DateTime.MinValue;

        Font textFont = new Font("Tahoma", 18);
        StringFormat textDirectionRTL = new StringFormat(StringFormatFlags.DirectionRightToLeft);
        Rectangle textLine1 = new Rectangle(10, 10, Settings.VideoWidth - 20, 40);
        Rectangle textLine2 = new Rectangle(10, Settings.VideoHeight - 10 - 40 - 40, Settings.VideoWidth - 20, 40);
        Rectangle textLine3 = new Rectangle(10, Settings.VideoHeight - 10 - 40, Settings.VideoWidth - 20, 40);

        SoundPlayer beepPlayer = new SoundPlayer(Resources.beep);

        public FormMain()
        {
            InitializeComponent();

            try
            {
                Directory.CreateDirectory(outputFolder);
            }
            catch (Exception ex)
            {
                Log.WriteLine(ex.ToString());
            }

            Hide();
        }

        private void FormMain_Load(object sender, EventArgs e)
        {
            // disable buttons for new session
            btnRecord.Enabled = false;
            btnPause.Enabled = false;
            btnStop.Enabled = false;
            btnReplay.Enabled = false;
            btnWriteDisk.Enabled = false;

            // init bitmap buffer
            _videoFrame = new Bitmap(Settings.VideoWidth, Settings.VideoHeight, PixelFormat.Format24bppRgb);

            // init grapics
            _graphics = Graphics.FromImage(_videoFrame);
#if USE_DIRECT_MEMORY_ACCESS
            _graphics.CompositingMode = CompositingMode.SourceOver;
#else
            _graphics.CompositingMode = CompositingMode.SourceCopy;
#endif
            _graphics.CompositingQuality = CompositingQuality.HighSpeed;
            _graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            _graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixel;
            _graphics.PixelOffsetMode = PixelOffsetMode.None;
            _graphics.SmoothingMode = SmoothingMode.None;

            // show new session
            using (FormNewSession formNewSession = new FormNewSession())
            {
                if (formNewSession.ShowDialog(this) == DialogResult.OK)
                {
                    sessionInfo = formNewSession.SessionInfo;
                    Log.WriteLine(sessionInfo.ToString());
                    Show();

                    PrepareRecord();

                    // show setting if it's the first time
                    if (!File.Exists(Environment.CurrentDirectory + "\\Settings.ini"))
                    {
                        using (FormSettings formSettings = new FormSettings())
                        {
                            formSettings.ShowDialog();
                            settings = formSettings.Settings;
                            settings.SaveSettings();
                            Log.WriteLine(settings.ToString());
                        }
                    }
                }
                else
                {
                    Close();
                }
            }
        }

        private void FormMain_Shown(object sender, EventArgs e)
        {
            // init render thread
            videoRenderThread = new Thread(VideoRenderWorker);
            videoRenderThread.Start();

            // start devices at startup
            InitDevices();
        }

        private void FormMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (videoRecordState != VideoRecordState.IDLE)
            {
                StopRecording();
            }
            StopDevices();
            StopRender();
        }

        private void VideoRenderWorker()
        {
            while (true)
            {
                if (isPreviewing)
                {
                    //Task.Factory.StartNew(() =>
                    //{
                    RenderVideoFrame();
                    //});
                }
            }
        }

        private void StopRender()
        {
            try
            {
                if (videoRenderThread != null)
                {
                    if (videoRenderThread.IsAlive)
                    {
                        videoRenderThread.Abort();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine(ex.ToString());
            }
        }

        private VideoCapabilities selectResolution(VideoCaptureDevice device)
        {
            foreach (var cap in device.VideoCapabilities)
            {
                if (cap.FrameSize.Width == 1280
                    && cap.FrameSize.Height == 720)
                    return cap;
            }
            return device.VideoCapabilities[0];
        }

        private void InitDevices()
        {
            // stop running devices
            try
            {
                StopDevices();
            }
            catch (Exception ex)
            {
                Log.WriteLine(ex.ToString());
            }

            // start audio device
            try
            {
                audioDevices = new List<AudioDeviceInfo>(new AudioDeviceCollection(AudioDeviceCategory.Capture));
                if (audioDevices != null && audioDevices.Count > 0)
                {
                    AudioDeviceInfo audioDeviceInfo = null;

                    // find last used device
                    foreach (AudioDeviceInfo device in audioDevices)
                    {
                        if (settings.AudioInputPath.Equals(device.Description + "|" + device.Guid.ToString()))
                        {
                            audioDeviceInfo = device;
                        }
                    }

                    // if nothing matched, use default device
                    if (audioDeviceInfo == null)
                    {
                        audioDeviceInfo = audioDevices[0];
                    }

                    // setup audio device
                    List<AudioCaptureDevice> listAudioDevices = new List<AudioCaptureDevice>();
                    AudioCaptureDevice audioCaptureDevice = new AudioCaptureDevice(audioDeviceInfo)
                    {
                        Format = SampleFormat.Format16Bit,
                        SampleRate = settings.AudioSampleRate, // 44100 Hz
                        DesiredFrameSize = settings.AudioFrameSize // 8 Kb
                    };
                    Log.WriteLine("audioCaptureDevice.Format = " + audioCaptureDevice.Format.ToString());
                    Log.WriteLine("audioCaptureDevice.SampleRate = " + audioCaptureDevice.SampleRate);
                    Log.WriteLine("audioCaptureDevice.DesiredFrameSize = " + audioCaptureDevice.DesiredFrameSize);
                    audioCaptureDevice.Start();
                    listAudioDevices.Add(audioCaptureDevice);

                    // setup audio mixer as main audio output device
                    audioDevice = new AudioSourceMixer(listAudioDevices);
                    audioDevice.NewFrame += AudioDevice_NewFrame;
                    audioDevice.Channels = (settings.AudioChannel == Settings.AudioMode.Mono ? 1 : 2);
                    audioDevice.Start();

                    Log.WriteLine(">>> START audioDevice: " + audioDeviceInfo.Description + "|" + audioDeviceInfo.Guid.ToString());
                }
                else
                {
                    MessageBox.Show("Không tìm thấy thiết bị thu âm thanh!");
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine(ex.ToString());
            }

            // start video devices
            string device1_MonikerString = "";
            string device2_MonikerString = "";
            try
            {
                videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                if (videoDevices != null && videoDevices.Count > 0)
                {
                    // find last used devices
                    foreach (FilterInfo device in videoDevices)
                    {
                        if (settings.Camera1_InputPath.Equals(device.MonikerString))
                        {
                            device1_MonikerString = device.MonikerString;
                        }

                        if (settings.Camera2_InputPath.Equals(device.MonikerString))
                        {
                            device2_MonikerString = device.MonikerString;
                        }
                    }

                    // not matched, use default device
                    if (device1_MonikerString.Equals(""))
                    {
                        device1_MonikerString = videoDevices[0].MonikerString;
                    }
                    videoDevice1 = new VideoCaptureDevice(device1_MonikerString);
                    for (int i = 0; i < videoDevice1.VideoCapabilities.Length; i++)
                    {
                        Log.WriteLine("VideoCapabilities " + i);
                        Log.WriteLine("Resolution = " + videoDevice1.VideoCapabilities[i].FrameSize.ToString());
                    }

                    videoDevice1.VideoResolution = selectResolution(videoDevice1);
                    //videoDevice1.DesiredAverageTimePerFrame = 330000; // ns
                    videoDevice1.NewFrame += VideoDevice1_NewFrame;
                    videoDevice1.Start();
                    Log.WriteLine(">>> START videoDevice1: " + device1_MonikerString);

                    // open second device if needed
                    if (videoDevices.Count > 1)
                    {
                        if (device2_MonikerString.Equals(""))
                        {
                            device2_MonikerString = videoDevices[1].MonikerString;
                        }
                    }
                    else
                    {
                        // fallback to Single mode
                        settings.SetVideoMixingMode(Settings.VideoMode.Single);
                    }

                    if (settings.VideoMixingMode != Settings.VideoMode.Single)
                    {
                        videoDevice2 = new VideoCaptureDevice(device2_MonikerString);
                        for (int i = 0; i < videoDevice2.VideoCapabilities.Length; i++)
                        {
                            Log.WriteLine("VideoCapabilities " + i);
                            Log.WriteLine("Resolution = " + videoDevice2.VideoCapabilities[i].FrameSize.ToString());
                        }
                        videoDevice2.VideoResolution = selectResolution(videoDevice2);
                        //videoDevice2.DesiredAverageTimePerFrame = 330000; // ns
                        videoDevice2.NewFrame += VideoDevice2_NewFrame;
                        videoDevice2.Start();
                        Log.WriteLine(">>> START videoDevice2: " + device2_MonikerString);
                    }
                }
                else
                {
                    MessageBox.Show("Không tìm thấy thiết bị thu hình ảnh!");
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine(ex.ToString());
            }

            // change buttons state
            if (videoDevice1 != null)
            {
                btnRecord.Enabled = true;
                btnPause.Enabled = false;
                btnStop.Enabled = false;
                btnReplay.Enabled = false;
                btnWriteDisk.Enabled = false;

                // reset record time
                UpdateRecordTime(0);

                isPreviewing = true;
            }

            Log.WriteLine(settings.ToString());
        }

        private void PausePreview()
        {
            if (audioDevice != null)
            {
                audioDevice.NewFrame -= AudioDevice_NewFrame;
            }
            if (videoDevice1 != null)
            {
                videoDevice1.NewFrame -= VideoDevice1_NewFrame;
            }
            if (videoDevice2 != null)
            {
                videoDevice2.NewFrame -= VideoDevice2_NewFrame;
            }
            isPreviewing = false;
        }

        private void ResumePreview()
        {
            if (isDevicesStopped())
            {
                InitDevices();
            }
            else
            {
                if (audioDevice != null)
                {
                    audioDevice.NewFrame += AudioDevice_NewFrame;
                }
                if (videoDevice1 != null)
                {
                    videoDevice1.NewFrame += VideoDevice1_NewFrame;
                }
                if (videoDevice2 != null)
                {
                    videoDevice2.NewFrame += VideoDevice2_NewFrame;
                }
            }
            isPreviewing = true;
        }

        private void StopDevices()
        {
            try
            {
                if (audioDevice != null)
                {
                    Log.WriteLine(">>> STOP audioDevice: start");
                    audioDevice.NewFrame -= AudioDevice_NewFrame;

                    audioDevice.SignalToStop();
                    //audioDevice.WaitForStop();
                    Thread.Sleep(1000);

                    audioDevice.Dispose();
                    audioDevice = null;
                    Log.WriteLine(">>> STOP audioDevice: done");
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine(ex.ToString());
                audioDevice = null;
            }

            try
            {
                if (videoDevice1 != null)
                {
                    Log.WriteLine(">>> STOP videoDevice1: start");
                    videoDevice1.NewFrame -= VideoDevice1_NewFrame;
                    videoDevice1.SignalToStop();
                    //videoDevice1.WaitForStop();
                    Thread.Sleep(1000);

                    videoDevice1 = null;
                    Log.WriteLine(">>> STOP videoDevice1: done");
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine(ex.ToString());
                videoDevice1 = null;
            }

            try
            {
                if (videoDevice2 != null)
                {
                    Log.WriteLine(">>> STOP videoDevice2: start");
                    videoDevice2.NewFrame -= VideoDevice2_NewFrame;
                    videoDevice2.SignalToStop();
                    //videoDevice2.WaitForStop();
                    Thread.Sleep(1000);

                    videoDevice2 = null;
                    Log.WriteLine(">>> STOP videoDevice2: done");
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine(ex.ToString());
                videoDevice2 = null;
            }
        }

        private bool isDevicesStopped()
        {
            return ((audioDevice != null) || (videoDevice1 != null) || (videoDevice2 != null));
        }

        delegate void UpdateLiveImageInvokerCallBack(PictureBox pictureBox, Bitmap bitmap);

        private void UpdateLiveImageInvoker(PictureBox pictureBox, Bitmap bitmap)
        {
            try
            {
                if (InvokeRequired)
                {
                    UpdateLiveImageInvokerCallBack invoker = new UpdateLiveImageInvokerCallBack(UpdateLiveImageInvoker);
                    Invoke(invoker, new object[] { pictureBox, bitmap });
                }
                else
                {
                    UpdateLiveImage(pictureBox, bitmap);
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine(ex.ToString());
            }
        }

        private void UpdateLiveImage(PictureBox pictureBox, Bitmap bitmap)
        {
            try
            {
                if (pictureBox.Image != null)
                {
                    pictureBox.Image.Dispose();
                }
                pictureBox.Image = bitmap;
            }
            catch (Exception ex)
            {
                Log.WriteLine(ex.ToString());
            }
        }

        private void AudioDevice_NewFrame(object sender, Accord.Audio.NewFrameEventArgs eventArgs)
        {
            // MICROPHONE DOES REPORT SIGNAL DURATION
            //if (true) // debug
            //{
            //    Log.WriteLine("Duration = " + eventArgs.Signal.Duration.Milliseconds);
            //    //Log.WriteLine("Length = " + eventArgs.Signal.Length);
            //    //Log.WriteLine("NumberOfChannels = " + eventArgs.Signal.NumberOfChannels);
            //    //Log.WriteLine("SampleRate = " + eventArgs.Signal.SampleRate);
            //    //Log.WriteLine("SampleFormat = " + eventArgs.Signal.SampleFormat.ToString());
            //}

            if (videoRecordState == VideoRecordState.RECORDING)
            {
                lock (syncRender)
                {
                    try
                    {
                        videoFileWriter.WriteAudioFrame(eventArgs.Signal.RawData);
                    }
                    catch (Exception ex)
                    {
                        Log.WriteLine(ex.ToString());
                    }
                }
            }
        }

        private void VideoDevice1_NewFrame(object sender, Accord.Video.NewFrameEventArgs eventArgs)
        {
            // WHY DOESN'T CAMERA REPORT FRAME DURATION? :(
            //if (true) // debug
            //{
            //    Log.WriteLine("CaptureStarted = " + eventArgs.CaptureStarted.ToString());
            //    Log.WriteLine("CaptureFinished = " + eventArgs.CaptureFinished.ToString());
            //    Log.WriteLine("FrameIndex = " + eventArgs.FrameIndex);
            //}

            lock (syncFrame1)
            {
                try
                {
                    if (frame1 != null)
                    {
                        frame1.Dispose();
                    }
                    frame1 = (Bitmap)eventArgs.Frame.Clone();
                }
                catch (Exception ex)
                {
                    Log.WriteLine(ex.ToString());
                }
                //Log.WriteLine("Frame 1: " + frame1.Width + "x" + frame1.Height);
            }
        }

        private void VideoDevice2_NewFrame(object sender, Accord.Video.NewFrameEventArgs eventArgs)
        {
            lock (syncFrame2)
            {
                try
                {
                    if (frame2 != null)
                    {
                        frame2.Dispose();
                    }
                    frame2 = (Bitmap)eventArgs.Frame.Clone();
                }
                catch (Exception ex)
                {
                    Log.WriteLine(ex.ToString());
                }
                //Log.WriteLine("Frame 2: " + frame2.Width + "x" + frame2.Height);
            }
        }

        Stopwatch stopWatch = new Stopwatch();
#if !USE_SLOW_PC
        int renderCount = 0;
        double totalRenderTime = 0;
#endif
        private void RenderVideoFrame()
        {
            stopWatch.Reset();
            stopWatch.Start();
#if !USE_SLOW_PC
            renderCount++;
#endif

#if USE_DIRECT_MEMORY_ACCESS
            // use unsafe and parallel
            unsafe
            {
                // lock video frame into memory
                BitmapData bitmapData_videoFrame = _videoFrame.LockBits(
                    new Rectangle(0, 0, _videoFrame.Width, _videoFrame.Height),
                    ImageLockMode.WriteOnly,
                    _videoFrame.PixelFormat);

                byte* ptrFirstPixel_videoFrame = (byte*)bitmapData_videoFrame.Scan0;
#else
            _graphics.CompositingMode = CompositingMode.SourceCopy;
#endif
                // render frame1 which comes from videoDevive1's thread, we need to lock it
                Bitmap _frame1 = null;
                lock (syncFrame1)
                {
                    try
                    {
                        if (frame1 != null)
                        {
                            _frame1 = (Bitmap)frame1.Clone();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.WriteLine(ex.ToString());
                    }
                }

                if (_frame1 != null)
                {
                    try
                    {
#if USE_DIRECT_MEMORY_ACCESS
                        // get raw data of frame 1
                        BitmapData bitmapData_frame1 = _frame1.LockBits(
                            new Rectangle(0, 0, _frame1.Width, _frame1.Height),
                            ImageLockMode.ReadOnly,
                            _frame1.PixelFormat);
                        byte* ptrFirstPixel_frame1 = (byte*)bitmapData_frame1.Scan0;

                        if (settings.VideoMixingMode == Settings.VideoMode.Single
                            || settings.VideoMixingMode == Settings.VideoMode.Overlay)
                        {
                            // copy all pixels of frame 1 to video frame
                            Parallel.For(0, bitmapData_videoFrame.Height, y =>
                            {
                                byte* currentLine_videoFrame = ptrFirstPixel_videoFrame + (y * bitmapData_videoFrame.Stride);
                                byte* currentLine_frame1 = ptrFirstPixel_frame1 + (y * bitmapData_frame1.Stride);
                                for (int x = 0; x < bitmapData_videoFrame.Stride; x++)
                                {
                                    currentLine_videoFrame[x] = currentLine_frame1[x];
                                };
                            });
                        }
                        else
                        {
                            // copy a half of pixels of frame 1 to video frame
                            Parallel.For(0, bitmapData_videoFrame.Height, y =>
                            {
                                byte* currentLine_videoFrame = ptrFirstPixel_videoFrame + (y * bitmapData_videoFrame.Stride);
                                byte* currentLine_frame1 = ptrFirstPixel_frame1 + (y * bitmapData_frame1.Stride);
                                int u = 0;
                                int v = 0;
                                for (int x = 0; x < bitmapData_videoFrame.Width / 2; x++)
                                {
                                    currentLine_videoFrame[u++] = currentLine_frame1[v++];
                                    currentLine_videoFrame[u++] = currentLine_frame1[v++];
                                    currentLine_videoFrame[u++] = currentLine_frame1[v++];
                                    v += 3;
                                };
                            });
                        }

                        _frame1.UnlockBits(bitmapData_frame1);
#else
                    _graphics.DrawImage(_frame1, settings.Frame1_X, settings.Frame1_Y, settings.Frame1_Width, settings.Frame1_Height);
#endif
                    }
                    catch (Exception ex)
                    {
                        Log.WriteLine(ex.ToString());
                    }
                    finally
                    {
                        _frame1.Dispose();
                    }
                }

                if (settings.VideoMixingMode != Settings.VideoMode.Single)
                {
                    // render frame2 which comes from videoDevive2's thread, we need to lock it
                    Bitmap _frame2 = null;
                    lock (syncFrame2)
                    {
                        try
                        {
                            if (frame2 != null)
                            {
                                _frame2 = (Bitmap)frame2.Clone();
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.WriteLine(ex.ToString());
                        }
                    }

                    if (_frame2 != null)
                    {
                        try
                        {
#if USE_DIRECT_MEMORY_ACCESS
                            // get raw data of frame 2
                            BitmapData bitmapData_frame2 = _frame2.LockBits(
                                new Rectangle(0, 0, _frame2.Width, _frame2.Height),
                                ImageLockMode.ReadOnly,
                                _frame2.PixelFormat);
                            byte* ptrFirstPixel_frame2 = (byte*)bitmapData_frame2.Scan0;

                            if (settings.VideoMixingMode == Settings.VideoMode.SideBySide)
                            {
                                // copy a half of pixels of frame 2 to video frame
                                Parallel.For(0, bitmapData_videoFrame.Height, y =>
                                {
                                    byte* currentLine_videoFrame = ptrFirstPixel_videoFrame + (y * bitmapData_videoFrame.Stride);
                                    byte* currentLine_frame2 = ptrFirstPixel_frame2 + (y * bitmapData_frame2.Stride);
                                    int u = (bitmapData_videoFrame.Width / 2) * 3;
                                    int v = 0;
                                    for (int x = 0; x < bitmapData_videoFrame.Width / 2; x++)
                                    {
                                        currentLine_videoFrame[u++] = currentLine_frame2[v++];
                                        currentLine_videoFrame[u++] = currentLine_frame2[v++];
                                        currentLine_videoFrame[u++] = currentLine_frame2[v++];
                                        v += 3;
                                    };
                                });
                            }
                            else
                            {
                                // start copy at overlay position only
                                Parallel.For(0, 240, y =>
                                {
                                    byte* currentLine_videoFrame = ptrFirstPixel_videoFrame + ((470 + y) * bitmapData_videoFrame.Stride);
                                    byte* currentLine_frame2 = ptrFirstPixel_frame2 + ((y * 3) * bitmapData_frame2.Stride);
                                    int u = 844 * 3;
                                    int v = 0;
                                    for (int x = 0; x < 426; x++)
                                    {
                                        currentLine_videoFrame[u++] = currentLine_frame2[v++];
                                        currentLine_videoFrame[u++] = currentLine_frame2[v++];
                                        currentLine_videoFrame[u++] = currentLine_frame2[v++];
                                        v += 3;
                                    };
                                });
                            }
                            _frame2.UnlockBits(bitmapData_frame2);
#else
                        _graphics.DrawImage(_frame2, settings.Frame2_X, settings.Frame2_Y, settings.Frame2_Width, settings.Frame2_Height);
#endif
                        }
                        catch (Exception ex)
                        {
                            Log.WriteLine(ex.ToString());
                        }
                        finally
                        {
                            _frame2.Dispose();
                        }
                    }
                }
#if USE_DIRECT_MEMORY_ACCESS
                _videoFrame.UnlockBits(bitmapData_videoFrame);
            }
#endif
            // add text
#if !USE_DIRECT_MEMORY_ACCESS
            _graphics.CompositingMode = CompositingMode.SourceOver;
#endif
            try
            {
                // TopLeft: Name1
                _graphics.DrawString(sessionInfo.Name1, textFont, Brushes.Red, textLine1);
                // TopRight: Name 2
                _graphics.DrawString(sessionInfo.Name2, textFont, Brushes.Red, textLine1, textDirectionRTL);
                // BottomLeft: Name 3, Name 4
                _graphics.DrawString(sessionInfo.Name3, textFont, Brushes.Red, textLine2);
                _graphics.DrawString(sessionInfo.Name4, textFont, Brushes.Red, textLine3);
                // BottomRight: Name 5 and DateTime
                _graphics.DrawString(sessionInfo.Name5, textFont, Brushes.Red, textLine2, textDirectionRTL);
                _graphics.DrawString(DateTime.Now.ToString("HH:mm:ss dd/MM/yyyy"), textFont, Brushes.Red, textLine3, textDirectionRTL);
            }
            catch (Exception ex)
            {
                Log.WriteLine(ex.ToString());
            }

            // show preview in UI thread, need to clone it, and then MUST invoke the UI thread
            if (pictureFrame != null)
            {
                UpdateLiveImageInvoker(pictureFrame, (Bitmap)_videoFrame.Clone());
            }

            // write frame if needed
            if (videoRecordState == VideoRecordState.RECORDING)
            {
                //Task.Factory.StartNew(() =>
                //{
                lock (syncRender)
                {
                    try
                    {
                        if (lastVideoFrameTime == DateTime.MinValue)
                        {
                            videoFileWriter.WriteVideoFrame(_videoFrame);
                            lastVideoFrameTime = DateTime.Now;
                        }
                        else
                        {
                            DateTime now = DateTime.Now;
                            TimeSpan timeSpan = now - lastVideoFrameTime;
                            videoFileWriter.WriteVideoFrame(_videoFrame, timeSpan);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.WriteLine(ex.ToString());
                    }
                }
                //});
            }

            stopWatch.Stop();
            int timeLeft = settings.VideoFrameDuration - (int)stopWatch.Elapsed.TotalMilliseconds;

#if !USE_SLOW_PC
            totalRenderTime += stopWatch.Elapsed.TotalMilliseconds;
            if (renderCount == 100)
            {
                Log.WriteLine(renderCount + " frames in average " + totalRenderTime / renderCount + " ms");
                renderCount = 0;
                totalRenderTime = 0;
            }
#endif
            if (timeLeft > 0)
            {
                Thread.Sleep(timeLeft);
            }
        }

        private void PrepareRecord()
        {
            recordPart = 0;
            try
            {
                File.Delete("records.txt");
            }
            catch (Exception ex)
            {
                Log.WriteLine(ex.ToString());
            }
        }

        private void StartRecording()
        {
            if (videoRecordState == VideoRecordState.IDLE)
            {
                VideoCodec videoCodec = VideoCodec.H264; /* MP4 container in H264 encode, H264 supports variant framerate */
                videoExtension = ".mp4";

                if (settings.VideoOutputFormat == Settings.VideoFormat.MPEG2)
                {
                    videoCodec = VideoCodec.MPEG2;
                    videoExtension = ".mpg";
                }

                outputFile = outputFolder + "\\" + sessionInfo.DateTime + "_" + recordPart.ToString() + videoExtension;
                videoFileWriter = new VideoFileWriter();

                if (audioDevice != null)
                {
                    Log.WriteLine("RECORD HAS AUDIO");

                    videoFileWriter.Open(
                        outputFile,
                        Settings.VideoWidth, Settings.VideoHeight, settings.VideoFrameRate, videoCodec, settings.VideoBitRate,
                        AudioCodec.MP3, settings.AudioBitRate, audioDevice.SampleRate, audioDevice.Channels
                    );
                }
                else
                {
                    videoFileWriter.Open(
                        outputFile,
                        Settings.VideoWidth, Settings.VideoHeight, settings.VideoFrameRate, videoCodec, settings.VideoBitRate
                    );
                }

                // track new file
                string command = "echo file '" + outputFile + "' >> records.txt";
                Log.WriteLine(command);

                Process process = new Process();
                ProcessStartInfo startInfo = new ProcessStartInfo();

                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = "/C " + command;
                process.StartInfo = startInfo;
                process.Start();
                process.WaitForExit();

                lastVideoFrameTime = DateTime.MinValue;
                timerRecord.Start();
                videoRecordState = VideoRecordState.RECORDING;
                Log.WriteLine(">>> START recording");
            }
        }

        private void PauseRecording()
        {
            if (videoRecordState == VideoRecordState.RECORDING)
            {
                videoRecordState = VideoRecordState.IDLE;

                lock (syncRender)
                {
                    try
                    {
                        if (videoFileWriter != null)
                        {
                            videoFileWriter.Flush();
                            videoFileWriter.Close();
                            videoFileWriter.Dispose();
                            videoFileWriter = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.WriteLine(ex.ToString());
                    }
                }

                timerRecord.Stop();
                Log.WriteLine(">>> PAUSE recording");
                recordPart++;
            }
        }

        private void StopRecording()
        {
            PauseRecording();

            Log.WriteLine(">>> MERGE VIDEO FILE");
            outputFile = outputFolder + "\\" + sessionInfo.DateTime + videoExtension;

            // join files
            if (File.Exists("ffmpeg.exe"))
            {
                String command = "ffmpeg.exe -f concat -safe 0 -i records.txt -c copy \"" + outputFile + "\"";
                Log.WriteLine(command);

                Process process = new Process();
                ProcessStartInfo startInfo = new ProcessStartInfo();

                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = "/C " + command;
                process.StartInfo = startInfo;
                process.Start();
                process.WaitForExit();

                // remove segmented files
                var dir = new DirectoryInfo(outputFolder);
                foreach (var file in dir.EnumerateFiles(sessionInfo.DateTime + "_*" + videoExtension))
                {
                    try
                    {
                        file.Delete();
                    }
                    catch (Exception ex)
                    {
                        Log.WriteLine(ex.ToString());
                    }
                }
            }
        }

        private void UpdateRecordTime(int start = -1)
        {
            if (start >= 0)
            {
                recordTime = start;
            }

            TimeSpan timeRun = TimeSpan.FromSeconds(recordTime);
            txtTimeRun.Text = timeRun.ToString("hh':'mm':'ss");

            TimeSpan timeLeft = TimeSpan.FromSeconds(sessionInfo.MaxTime - recordTime);
            txtTimeLeft.Text = timeLeft.ToString("hh':'mm':'ss");

            if (timeLeft.TotalSeconds < 600) /* less than 10 mins */
            {
                // we can use default windows sound, select one of below
                // user can disable default windows sound via sound settings !!!
                //SystemSounds.Asterisk.Play();
                //SystemSounds.Beep.Play();
                //SystemSounds.Exclamation.Play();
                //SystemSounds.Hand.Play();
                //SystemSounds.Question.Play();

                // use our sound
                beepPlayer.Play();
                txtTimeLeft.ForeColor = Color.Red;
            }

            if (timeLeft.TotalSeconds == 0)
            {
                btnPause.PerformClick();
                btnRecord.Enabled = false;
                MessageBox.Show("Đã quá thời lượng ghi tối đa." + Environment.NewLine + "Vui lòng nhấn kết thúc!", "Thông báo", MessageBoxButtons.OK);
            }

            if (videoRecordState == VideoRecordState.RECORDING)
            {
                if (recordTime % 2 == 0)
                {
                    signalRecord.BackgroundImage = global::VACamera.Properties.Resources.rec_on;
                }
                else
                {
                    signalRecord.BackgroundImage = global::VACamera.Properties.Resources.rec_off;
                }
            }
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Thoát ứng dụng?", "Cảnh báo", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                Close();
            }
        }

        private void newSessionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Hide();

            using (FormNewSession formNewSession = new FormNewSession())
            {
                if (formNewSession.ShowDialog(this) == DialogResult.OK)
                {
                    sessionInfo = formNewSession.SessionInfo;
                    settingsToolStripMenuItem.Enabled = true;
                    PrepareRecord();

                    // debug
                    Log.WriteLine(sessionInfo.ToString());
                    Log.WriteLine(settings.ToString());
                }
            }

            Show();

            if (videoDevice1 != null)
            {
                btnRecord.Enabled = true;
                btnPause.Enabled = false;
                btnStop.Enabled = false;
                btnReplay.Enabled = false;
                btnWriteDisk.Enabled = false;
            }

            UpdateRecordTime(0);

            //InitDevices(); // this process is slow
            ResumePreview();
        }

        private void settingsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (FormSettings formSettings = new FormSettings())
            {
                DialogResult result = formSettings.ShowDialog(this);
                settings = formSettings.Settings;
                if (result == DialogResult.OK)
                {
                    InitDevices();
                }
            }
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (FormAbout formAbout = new FormAbout())
            {
                if (formAbout.ShowDialog(this) == DialogResult.OK)
                {

                }
            }
        }

        private void btnRecord_Click(object sender, EventArgs e)
        {
            if (videoRecordState == VideoRecordState.IDLE)
            {
                btnRecord.Enabled = false;
                btnPause.Enabled = true;
                btnStop.Enabled = true;
                btnReplay.Enabled = false;
                btnWriteDisk.Enabled = false;

                settingsToolStripMenuItem.Enabled = false; // do not change settings during recording
                signalRecord.BackgroundImage = global::VACamera.Properties.Resources.rec_on;

                StartRecording();
            }
        }

        private void btnPause_Click(object sender, EventArgs e)
        {
            if (videoRecordState == VideoRecordState.RECORDING)
            {
                btnRecord.Enabled = true;
                btnPause.Enabled = false;
                btnStop.Enabled = true;
                btnReplay.Enabled = true;
                btnWriteDisk.Enabled = false;

                signalRecord.BackgroundImage = global::VACamera.Properties.Resources.rec_pause;

                PauseRecording();
            }
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Kết thúc ghi hình và ghi DVD?", "Xác nhận", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                btnRecord.Enabled = false; // must start new session
                btnPause.Enabled = false;
                btnStop.Enabled = false;
                btnReplay.Enabled = true;
                btnWriteDisk.Enabled = true;

                settingsToolStripMenuItem.Enabled = true; // can change settings again
                signalRecord.BackgroundImage = null;

                StopRecording();
                Thread.Sleep(1000);

                //StopDevices(); // this method is slow
                PausePreview();

                btnWriteDisk_Click(btnWriteDisk, EventArgs.Empty);
            }
        }

        private void btnReplay_Click(object sender, EventArgs e)
        {
            if (videoRecordState == VideoRecordState.IDLE)
            {
                if (File.Exists(outputFile))
                {
                    Process.Start(outputFile);
                }
            }
        }

        private void btnWriteDisk_Click(object sender, EventArgs e)
        {
            Log.WriteLine(sessionInfo.ToString());
            Log.WriteLine("outputFile = " + outputFile);
            using (FormDvdWriter formDvdWriter = new FormDvdWriter(sessionInfo.DateTime, outputFile))
            {
                DialogResult result = formDvdWriter.ShowDialog();
                if (result == DialogResult.Yes)
                {
                    newSessionToolStripMenuItem_Click(newSessionToolStripMenuItem, EventArgs.Empty);
                }
                else if (result == DialogResult.No)
                {
                    MessageBox.Show("Kết thúc phiên làm việc và dừng chương trình!");
                    Close();
                }
                else if (result == DialogResult.Cancel)
                {
                    MessageBox.Show("Vui lòng tạo phiên làm việc mới!");
                }
            }
        }

        private void timerRecord_Tick(object sender, EventArgs e)
        {
            recordTime++;
            UpdateRecordTime();
        }

        private void FormMain_KeyDown(object sender, KeyEventArgs e)
        {
            Log.WriteLine(e.KeyCode.ToString());
            switch (e.KeyCode)
            {
                case Keys.F5:
                    btnRecord.PerformClick(); // won't perform if button is disabled
                    break;
                case Keys.F6:
                    btnPause.PerformClick();
                    break;
                case Keys.F7:
                    btnReplay.PerformClick();
                    break;
                case Keys.F8:
                    btnStop.PerformClick();
                    break;
                default:
                    break;
            }
        }
    }
}
