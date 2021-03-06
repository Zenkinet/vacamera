﻿using IMAPI2.Interop;
using IMAPI2.MediaItem;
using SharpDX.DirectSound;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;

namespace VACamera
{
    public partial class FormDvdWriter : Form
    {
        IMAPI_BURN_VERIFICATION_LEVEL _verificationLevel = IMAPI_BURN_VERIFICATION_LEVEL.IMAPI_BURN_VERIFICATION_NONE;
        bool _isBurning1 = false;
        bool _isBurning2 = false;
        bool _isBurning3 = false;

        BurnData _burnData1 = new BurnData();
        BurnData _burnData2 = new BurnData();
        FileCopier _fileCopier = new FileCopier();

        bool _isRecordSuccess1 = false;
        bool _isRecordSuccess2 = false;

        bool _closeMedia = true;
        bool _ejectMedia = true;

        string _sessionName = "";
        string _filePath = "";

        static readonly Object syncData = new Object();

        public FormDvdWriter(string sessionName, string filePath)
        {
            _sessionName = sessionName;
            _filePath = filePath;
            _fileCopier.OnProgressChanged += _fileCopier_OnProgressChanged;
            _fileCopier.OnComplete += _fileCopier_OnComplete;
            InitializeComponent();
        }

        private void FormDvdWriter_Load(object sender, EventArgs e)
        {
            bool isHavingDisk = false;
            bool isHaveUSB = false;

            txtFilename.Text = Path.GetFileName(_filePath);

            long filesize = (new FileInfo(_filePath)).Length;

            txtFileSize.Text = (filesize < 1000000000 ?
                        string.Format("{0} MB", filesize / 1000000) :
                        string.Format("{0:F2} GB", (float)filesize / 1000000000.0));

            // Determine the current recording devices
            MsftDiscMaster2 discMaster = null;
            try
            {
                discMaster = new MsftDiscMaster2();

                if (discMaster.IsSupportedEnvironment)
                {
                    foreach (string uniqueRecorderId in discMaster)
                    {
                        var discRecorder2 = new MsftDiscRecorder2();
                        discRecorder2.InitializeDiscRecorder(uniqueRecorderId);

                        listDrive1.Items.Add(discRecorder2);
                        listDrive2.Items.Add(discRecorder2);
                    }

                    if (listDrive1.Items.Count <= 0)
                    {
                        listDrive1.Enabled = false;
                        listDrive1.SelectedIndex = -1;

                        listDrive2.Enabled = false;
                        listDrive2.SelectedIndex = -1;
                    }
                    else if (listDrive1.Items.Count == 1)
                    {
                        listDrive1.SelectedIndex = 0;

                        listDrive2.Enabled = false;
                        listDrive2.SelectedIndex = 0;

                        isHavingDisk = true;
                    }
                    else
                    {
                        listDrive1.SelectedIndex = 0;

                        listDrive2.SelectedIndex = 1;
                        isHavingDisk = true;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Thiết bị không hỗ trợ thư viện IMAPI2");
                Log.WriteLine(ex.ToString());
                return;
            }
            finally
            {
                if (discMaster != null)
                {
                    Marshal.ReleaseComObject(discMaster);
                }
            }

            var driveList = DriveInfo.GetDrives();

            foreach (DriveInfo drive in driveList)
            {
                if (drive.DriveType == DriveType.Removable)
                {
                    listDrive3.Items.Add(drive.Name);
                }

                if (listDrive3.Items.Count > 0)
                {
                    listDrive3.SelectedIndex = 0;
                    isHaveUSB = true;
                    btnWrite3.Enabled = true;
                } else
                {
                    btnWrite3.Enabled = false;
                }
            }

            if (!(isHaveUSB || isHaveUSB))
            {
                MessageBox.Show("Không tìm thấy ổ đĩa/USB phù hợp");
                DialogResult = DialogResult.Cancel;
                Hide();
                Close();
            }

            // Create the volume label based on the current date if needed
            if (_sessionName.Equals(""))
            {
                _sessionName = DateTime.Now.ToString("yyyyMMdd_HHmm");
            }
        }

        private void FormDvdWriter_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                foreach (MsftDiscRecorder2 discRecorder2 in listDrive1.Items)
                {
                    if (discRecorder2 != null)
                    {
                        try
                        {
                            Marshal.ReleaseComObject(discRecorder2);
                        }
                        catch (Exception ex)
                        {
                            Log.WriteLine(ex.ToString());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine(ex.ToString());
            }
        }

        private void listDrive_Format(object sender, ListControlConvertEventArgs e)
        {
            IDiscRecorder2 discRecorder2 = (IDiscRecorder2)e.ListItem;
            string devicePaths = string.Empty;
            string volumePath = (string)discRecorder2.VolumePathNames.GetValue(0);
            foreach (string volPath in discRecorder2.VolumePathNames)
            {
                if (!string.IsNullOrEmpty(devicePaths))
                {
                    devicePaths += ",";
                }
                devicePaths += volumePath;
            }

            e.Value = string.Format("{0} [{1}]", devicePaths, discRecorder2.ProductId);
        }

        private void btnWrite1_Click(object sender, EventArgs e)
        {
            if (listDrive1.SelectedIndex == -1)
            {
                return;
            }

            if (_isBurning1)
            {
                btnWrite1.Enabled = true;
                backgroundBurnWorker1.CancelAsync();
            }
            else
            {
                btnWrite1.Enabled = false;
                _isBurning1 = true;
                txtStatus1.Text = "Đang ghi...";

                var discRecorder = (IDiscRecorder2)listDrive1.Items[listDrive1.SelectedIndex];
                _burnData1.uniqueRecorderId = discRecorder.ActiveDiscRecorder;

                Log.WriteLine("START Burning DISK 1");
                backgroundBurnWorker1.RunWorkerAsync(_burnData1);
            }
        }

        private void btnWrite2_Click(object sender, EventArgs e)
        {
            if (listDrive2.SelectedIndex == -1)
            {
                return;
            }

            if (_isBurning2)
            {
                btnWrite2.Enabled = false;
                backgroundBurnWorker2.CancelAsync();
            }
            else
            {
                _isBurning2 = true;
                txtStatus2.Text = "Đang ghi...";

                var discRecorder = (IDiscRecorder2)listDrive2.Items[listDrive2.SelectedIndex];
                _burnData2.uniqueRecorderId = discRecorder.ActiveDiscRecorder;

                Log.WriteLine("START Burning DISK 2");
                backgroundBurnWorker2.RunWorkerAsync(_burnData2);
            }

        }

        string _dest_path = "";

        private void btnWrite3_Click(object sender, EventArgs e)
        {
            if (listDrive3.SelectedIndex == -1)
            {
                return;
            }

            if (_isBurning3)
            {
                btnWrite3.Enabled = false;
                _copy_canceled = true;
                backgroundBurnWorker3.CancelAsync();
            }
            else
            {
                _isBurning3 = true;
                _copy_canceled = false;
                txtStatus3.Text = "Đang ghi...";
                _dest_path = listDrive3.Text + txtFilename.Text;

                Log.WriteLine("START Writing to USB");
                Console.WriteLine(_filePath);
                Console.WriteLine(_dest_path);

                backgroundBurnWorker3.RunWorkerAsync();
            }
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            if (_isBurning1 || _isBurning2 || _isBurning3)
            {
                if (MessageBox.Show("Hủy ghi đĩa?", "Cảnh báo", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    try
                    {
                        backgroundBurnWorker1.CancelAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.WriteLine(ex.ToString());
                    }

                    try
                    {
                        backgroundBurnWorker2.CancelAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.WriteLine(ex.ToString());
                    }

                    try
                    {
                        _copy_canceled = true;
                        backgroundBurnWorker3.CancelAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.WriteLine(ex.ToString());
                    }

                    Close();
                }
            }
            else
            {
                doAfterBurnWork();
            }
        }

        private void btnWriteAll_Click(object sender, EventArgs e)
        {
            if (btnWrite1.Enabled && listDrive1.SelectedIndex != -1)
            {
                btnWrite1_Click(btnWrite1, EventArgs.Empty);
                btnWrite1.Enabled = false;
                btnWriteAll.Enabled = false;
                btnCancel.Enabled = false;
            }

            //if (btnWrite2.Enabled && listDrive2.SelectedIndex != -1)
            //{
            //    btnWrite2_Click(btnWrite2, EventArgs.Empty);
            //    btnWrite2.Enabled = false;
            //    btnWriteAll.Enabled = false;
            //    btnCancel.Enabled = false;
            //}

            if (btnWrite3.Enabled && listDrive3.SelectedIndex != -1)
            {
                btnWrite3_Click(btnWrite3, EventArgs.Empty);
                btnWrite3.Enabled = false;
                btnWriteAll.Enabled = false;
                btnCancel.Enabled = false;
            }
        }

        private void backgroundBurnWorker1_DoWork(object sender, DoWorkEventArgs e)
        {
            MsftDiscRecorder2 discRecorder = null;
            MsftDiscFormat2Data discFormatData = null;
            try
            {
                // Create and initialize the IDiscRecorder2 object
                discRecorder = new MsftDiscRecorder2();
                var burnData = (BurnData)e.Argument;
                try
                {
                    Log.WriteLine("DISK 1 uniqueRecorderId = " + burnData.uniqueRecorderId);
                    discRecorder.InitializeDiscRecorder(burnData.uniqueRecorderId);
                }
                catch (Exception ex)
                {
                    e.Result = -1;
                    Log.WriteLine(ex.ToString());
                    return;
                }

                // Create and initialize the IDiscFormat2Data
                discFormatData = new MsftDiscFormat2Data
                {
                    Recorder = discRecorder,
                    ClientName = "VACamera",
                    ForceMediaToBeClosed = _closeMedia
                };

                // Set the verification level
                var burnVerification = (IBurnVerification)discFormatData;
                burnVerification.BurnVerificationLevel = _verificationLevel;

                // Check if media is blank, (for RW media)
                object[] multisessionInterfaces = null;
                if (!discFormatData.MediaHeuristicallyBlank)
                {
                    multisessionInterfaces = discFormatData.MultisessionInterfaces;
                }

                // Create the file system
                IStream fileSystem;
                if (!createMediaFileSystem(discRecorder, multisessionInterfaces, out fileSystem))
                {
                    e.Result = -1;
                    Log.WriteLine("Cannot create filesystem on disk!");
                    return;
                }

                // add the Update event handler
                discFormatData.Update += discFormatData1_Update;

                // Write the data here
                try
                {
                    discFormatData.Write(fileSystem);
                    e.Result = 0;
                }
                catch (Exception ex)
                {
                    e.Result = -1;
                    Log.WriteLine(ex.ToString());
                }
                finally
                {
                    if (fileSystem != null)
                    {
                        Marshal.FinalReleaseComObject(fileSystem);
                    }
                }

                // remove the Update event handler
                discFormatData.Update -= discFormatData1_Update;

                if (_ejectMedia)
                {
                    discRecorder.EjectMedia();
                }
            }
            catch (Exception ex)
            {
                e.Result = -1;
                Log.WriteLine(ex.ToString());
            }
            finally
            {
                if (discRecorder != null)
                {
                    Marshal.ReleaseComObject(discRecorder);
                }

                if (discFormatData != null)
                {
                    Marshal.ReleaseComObject(discFormatData);
                }
            }
        }

        private void backgroundBurnWorker2_DoWork(object sender, DoWorkEventArgs e)
        {
            MsftDiscRecorder2 discRecorder = null;
            MsftDiscFormat2Data discFormatData = null;
            try
            {
                // Create and initialize the IDiscRecorder2 object
                discRecorder = new MsftDiscRecorder2();
                var burnData = (BurnData)e.Argument;
                try
                {
                    Log.WriteLine("DISK 2 uniqueRecorderId = " + burnData.uniqueRecorderId);
                    discRecorder.InitializeDiscRecorder(burnData.uniqueRecorderId);
                }
                catch (Exception ex)
                {
                    e.Result = -1;
                    Log.WriteLine(ex.ToString());
                    return;
                }

                // Create and initialize the IDiscFormat2Data
                discFormatData = new MsftDiscFormat2Data
                {
                    Recorder = discRecorder,
                    ClientName = "VACamera",
                    ForceMediaToBeClosed = _closeMedia
                };

                // Set the verification level
                var burnVerification = (IBurnVerification)discFormatData;
                burnVerification.BurnVerificationLevel = _verificationLevel;

                // Check if media is blank, (for RW media)
                object[] multisessionInterfaces = null;
                if (!discFormatData.MediaHeuristicallyBlank)
                {
                    multisessionInterfaces = discFormatData.MultisessionInterfaces;
                }

                // Create the file system
                IStream fileSystem;
                if (!createMediaFileSystem(discRecorder, multisessionInterfaces, out fileSystem))
                {
                    e.Result = -1;
                    Log.WriteLine("Cannot create filesystem on disk!");
                    return;
                }

                // add the Update event handler
                discFormatData.Update += discFormatData2_Update;

                // Write the data here
                try
                {
                    discFormatData.Write(fileSystem);
                    e.Result = 0;
                }
                catch (Exception ex)
                {
                    e.Result = -1;
                    Log.WriteLine(ex.ToString());
                }
                finally
                {
                    if (fileSystem != null)
                    {
                        Marshal.FinalReleaseComObject(fileSystem);
                    }
                }

                // remove the Update event handler
                discFormatData.Update -= discFormatData2_Update;

                if (_ejectMedia)
                {
                    discRecorder.EjectMedia();
                }
            }
            catch (Exception ex)
            {
                e.Result = -1;
                Log.WriteLine(ex.ToString());
            }
            finally
            {
                if (discRecorder != null)
                {
                    Marshal.ReleaseComObject(discRecorder);
                }

                if (discFormatData != null)
                {
                    Marshal.ReleaseComObject(discFormatData);
                }
            }
        }

        private void _fileCopier_OnComplete()
        {
            throw new NotImplementedException();
        }

        bool _copy_canceled = false;

        private void _fileCopier_OnProgressChanged(double Persentage, ref bool Cancel)
        {
            Cancel = _copy_canceled;
            backgroundBurnWorker3.ReportProgress((int)Persentage);
        }

        private void backgroundBurnWorker3_DoWork(object sender, DoWorkEventArgs e)
        {
            Console.WriteLine("DO WORK ON USB");
            _fileCopier.Copy(_filePath, _dest_path);
        }

        private void backgroundBurnWorker1_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            var burnData = (BurnData)e.UserState;
            if (burnData.task == BURN_MEDIA_TASK.BURN_MEDIA_TASK_FILE_SYSTEM)
            {
                txtStatus1.Text = burnData.statusMessage;
            }
            else if (burnData.task == BURN_MEDIA_TASK.BURN_MEDIA_TASK_WRITING)
            {
                switch (burnData.currentAction)
                {
                    case IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_VALIDATING_MEDIA:
                        txtStatus1.Text = "Định dạng đĩa.";
                        break;

                    case IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_FORMATTING_MEDIA:
                        txtStatus1.Text = "Định dạng đĩa..";
                        break;

                    case IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_INITIALIZING_HARDWARE:
                        txtStatus1.Text = "Định dạng đĩa...";
                        break;

                    case IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_CALIBRATING_POWER:
                        txtStatus1.Text = "Định dạng đĩa....";
                        break;

                    case IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_WRITING_DATA:
                        long writtenSectors = burnData.lastWrittenLba - burnData.startLba;

                        if (writtenSectors > 0 && burnData.sectorCount > 0)
                        {
                            var percent = (int)((100 * writtenSectors) / burnData.sectorCount);
                            txtStatus1.Text = string.Format("Tiến trình: {0}%", percent);
                            progressBar1.Value = percent;
                            //TimeSpan timeRun = TimeSpan.FromSeconds(_burnData1.remainingTime);
                            //txtTimeLeft1.Text = timeRun.ToString("mm':'ss");
                        }
                        else
                        {
                            txtStatus1.Text = "Tiến trình 0%";
                            progressBar1.Value = 0;
                        }
                        break;

                    case IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_FINALIZATION:
                        txtStatus1.Text = "Đang hoàn tất...";
                        break;

                    case IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_COMPLETED:
                        txtStatus1.Text = "Đã hoàn thành!";
                        break;

                    case IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_VERIFYING:
                        txtStatus1.Text = "Đang kiểm tra";
                        break;
                }
            }
        }

        private void backgroundBurnWorker2_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            var burnData = (BurnData)e.UserState;
            if (burnData.task == BURN_MEDIA_TASK.BURN_MEDIA_TASK_FILE_SYSTEM)
            {
                txtStatus2.Text = burnData.statusMessage;
            }
            else if (burnData.task == BURN_MEDIA_TASK.BURN_MEDIA_TASK_WRITING)
            {
                switch (burnData.currentAction)
                {
                    case IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_VALIDATING_MEDIA:
                        txtStatus2.Text = "Định dạng đĩa.";
                        break;

                    case IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_FORMATTING_MEDIA:
                        txtStatus2.Text = "Định dạng đĩa..";
                        break;

                    case IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_INITIALIZING_HARDWARE:
                        txtStatus2.Text = "Định dạng đĩa...";
                        break;

                    case IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_CALIBRATING_POWER:
                        txtStatus2.Text = "Định dạng đĩa....";
                        break;

                    case IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_WRITING_DATA:
                        long writtenSectors = burnData.lastWrittenLba - burnData.startLba;

                        if (writtenSectors > 0 && burnData.sectorCount > 0)
                        {
                            var percent = (int)((100 * writtenSectors) / burnData.sectorCount);
                            txtStatus2.Text = string.Format("Tiến trình: {0}%", percent);
                            progressBar2.Value = percent;
                            //TimeSpan timeRun = TimeSpan.FromSeconds(_burnData2.remainingTime);
                            //txtTimeLeft2.Text = timeRun.ToString("mm':'ss");
                        }
                        else
                        {
                            txtStatus2.Text = "Tiến trình 0%";
                            progressBar2.Value = 0;
                        }
                        break;

                    case IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_FINALIZATION:
                        txtStatus2.Text = "Đang hoàn tất...";
                        break;

                    case IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_COMPLETED:
                        txtStatus2.Text = "Đã hoàn thành!";
                        break;

                    case IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_VERIFYING:
                        txtStatus2.Text = "Đang kiểm tra";
                        break;
                }
            }
        }

        private void backgroundBurnWorker3_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            progressBar3.Value = e.ProgressPercentage;
        }

        private void backgroundBurnWorker1_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            txtStatus1.Text = (int)e.Result == 0 ? "Đã ghi xong!" : "Có lỗi trong quá trình ghi đĩa!";
            //progressBar1.Value = 0;

            _isBurning1 = false;
            _isRecordSuccess1 = ((int)e.Result == 0);
            btnWrite1.Enabled = true;

            doAfterBurnWork();
        }

        private void backgroundBurnWorker2_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            txtStatus2.Text = (int)e.Result == 0 ? "Đã ghi xong!" : "Có lỗi trong quá trình ghi đĩa!";
            //progressBar2.Value = 0;

            _isBurning2 = false;
            _isRecordSuccess2 = ((int)e.Result == 0);
            btnWrite2.Enabled = true;

            doAfterBurnWork();
        }

        private void backgroundBurnWorker3_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            txtStatus3.Text = "Hoàn tất";
            //progressBar3.Value = 0;

            _isBurning3 = false;
            btnWrite3.Enabled = true;

            doAfterBurnWork();
        }

        void discFormatData1_Update([In, MarshalAs(UnmanagedType.IDispatch)] object sender, [In, MarshalAs(UnmanagedType.IDispatch)] object progress)
        {
            //
            // Check if we've cancelled
            //
            if (backgroundBurnWorker1.CancellationPending)
            {
                var format2Data = (IDiscFormat2Data)sender;
                format2Data.CancelWrite();
                return;
            }

            var eventArgs = (IDiscFormat2DataEventArgs)progress;

            _burnData1.task = BURN_MEDIA_TASK.BURN_MEDIA_TASK_WRITING;

            // IDiscFormat2DataEventArgs Interface
            _burnData1.elapsedTime = eventArgs.ElapsedTime;
            //_burnData1.remainingTime = eventArgs.RemainingTime;
            _burnData1.totalTime = eventArgs.TotalTime;

            // IWriteEngine2EventArgs Interface
            _burnData1.currentAction = eventArgs.CurrentAction;
            _burnData1.startLba = eventArgs.StartLba;
            _burnData1.sectorCount = eventArgs.SectorCount;
            _burnData1.lastReadLba = eventArgs.LastReadLba;
            _burnData1.lastWrittenLba = eventArgs.LastWrittenLba;
            _burnData1.totalSystemBuffer = eventArgs.TotalSystemBuffer;
            _burnData1.usedSystemBuffer = eventArgs.UsedSystemBuffer;
            _burnData1.freeSystemBuffer = eventArgs.FreeSystemBuffer;

            //
            // Report back to the UI
            //
            backgroundBurnWorker1.ReportProgress(0, _burnData1);
        }

        void discFormatData2_Update([In, MarshalAs(UnmanagedType.IDispatch)] object sender, [In, MarshalAs(UnmanagedType.IDispatch)] object progress)
        {
            //
            // Check if we've cancelled
            //
            if (backgroundBurnWorker2.CancellationPending)
            {
                var format2Data = (IDiscFormat2Data)sender;
                format2Data.CancelWrite();
                return;
            }

            var eventArgs = (IDiscFormat2DataEventArgs)progress;

            _burnData2.task = BURN_MEDIA_TASK.BURN_MEDIA_TASK_WRITING;

            // IDiscFormat2DataEventArgs Interface
            _burnData2.elapsedTime = eventArgs.ElapsedTime;
            //_burnData2.remainingTime = eventArgs.RemainingTime;
            _burnData2.totalTime = eventArgs.TotalTime;

            // IWriteEngine2EventArgs Interface
            _burnData2.currentAction = eventArgs.CurrentAction;
            _burnData2.startLba = eventArgs.StartLba;
            _burnData2.sectorCount = eventArgs.SectorCount;
            _burnData2.lastReadLba = eventArgs.LastReadLba;
            _burnData2.lastWrittenLba = eventArgs.LastWrittenLba;
            _burnData2.totalSystemBuffer = eventArgs.TotalSystemBuffer;
            _burnData2.usedSystemBuffer = eventArgs.UsedSystemBuffer;
            _burnData2.freeSystemBuffer = eventArgs.FreeSystemBuffer;

            //
            // Report back to the UI
            //
            backgroundBurnWorker2.ReportProgress(0, _burnData2);
        }

        private bool createMediaFileSystem(IDiscRecorder2 discRecorder, object[] multisessionInterfaces, out IStream dataStream)
        {
            MsftFileSystemImage fileSystemImage = null;
            try
            {
                fileSystemImage = new MsftFileSystemImage();
                fileSystemImage.ChooseImageDefaults(discRecorder);
                fileSystemImage.FileSystemsToCreate = FsiFileSystems.FsiFileSystemJoliet | FsiFileSystems.FsiFileSystemISO9660;
                fileSystemImage.VolumeName = _sessionName;

                // If multisessions, then import previous sessions
                if (multisessionInterfaces != null)
                {
                    try
                    {
                        fileSystemImage.MultisessionInterfaces = multisessionInterfaces;
                        fileSystemImage.ImportFileSystem();
                    }
                    catch (Exception ex)
                    {
                        // ignore multisession
                        Log.WriteLine(ex.ToString());
                    }
                }

                // Get the image root
                IFsiDirectoryItem rootItem = fileSystemImage.Root;

                // Burn disks in parallel could make the file in burning is lock
                lock (syncData)
                {
                    // Add Files and Directories to File System Image
                    var fileItem = new FileItem(_filePath);
                    IMediaItem mediaItem = fileItem;
                    mediaItem.AddToFileSystem(rootItem);

                    // Make data stream
                    try
                    {
                        dataStream = fileSystemImage.CreateResultImage().ImageStream;
                    }
                    catch (Exception ex)
                    {
                        dataStream = null;
                        MessageBox.Show("Ổ đĩa bị khóa hoặc có lỗi trong quá trình định dạng đĩa");
                        Log.WriteLine(ex.ToString());
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                dataStream = null;
                MessageBox.Show("Ổ đĩa bị khóa hoặc có lỗi trong quá trình định dạng đĩa");
                Log.WriteLine(ex.ToString());
                return false;
            }
            finally
            {
                if (fileSystemImage != null)
                {
                    Marshal.ReleaseComObject(fileSystemImage);
                }
            }

            return true;
        }

        private static string GetMediaTypeString(IMAPI_MEDIA_PHYSICAL_TYPE mediaType)
        {
            switch (mediaType)
            {
                case IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_UNKNOWN:
                default:
                    return "Unknown Media Type";

                case IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_CDROM:
                    return "CD-ROM";

                case IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_CDR:
                    return "CD-R";

                case IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_CDRW:
                    return "CD-RW";

                case IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_DVDROM:
                    return "DVD ROM";

                case IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_DVDRAM:
                    return "DVD-RAM";

                case IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_DVDPLUSR:
                    return "DVD+R";

                case IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_DVDPLUSRW:
                    return "DVD+RW";

                case IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_DVDPLUSR_DUALLAYER:
                    return "DVD+R Dual Layer";

                case IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_DVDDASHR:
                    return "DVD-R";

                case IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_DVDDASHRW:
                    return "DVD-RW";

                case IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_DVDDASHR_DUALLAYER:
                    return "DVD-R Dual Layer";

                case IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_DISK:
                    return "random-access writes";

                case IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_DVDPLUSRW_DUALLAYER:
                    return "DVD+RW DL";

                case IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_HDDVDROM:
                    return "HD DVD-ROM";

                case IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_HDDVDR:
                    return "HD DVD-R";

                case IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_HDDVDRAM:
                    return "HD DVD-RAM";

                case IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_BDROM:
                    return "Blu-ray DVD (BD-ROM)";

                case IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_BDR:
                    return "Blu-ray media";

                case IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_BDRE:
                    return "Blu-ray Rewritable media";
            }
        }

        private bool detectMedia1()
        {
            if (listDrive1.SelectedIndex == -1)
            {
                txtStatus1.Text = "Không có ổ đĩa";
                return false;
            }

            var discRecorder = (IDiscRecorder2)listDrive1.Items[listDrive1.SelectedIndex];

            MsftFileSystemImage fileSystemImage = null;
            MsftDiscFormat2Data discFormatData = null;

            try
            {
                // Create and initialize the IDiscFormat2Data
                discFormatData = new MsftDiscFormat2Data();
                if (!discFormatData.IsCurrentMediaSupported(discRecorder))
                {
                    txtStatus1.Text = "Ổ đĩa không có chức năng ghi";
                    return false;
                }
                else
                {
                    // Get the media type in the recorder
                    discFormatData.Recorder = discRecorder;
                    IMAPI_MEDIA_PHYSICAL_TYPE mediaType = discFormatData.CurrentPhysicalMediaType;
                    txtStatus1.Text = GetMediaTypeString(mediaType);

                    // Create a file system and select the media type
                    fileSystemImage = new MsftFileSystemImage();
                    fileSystemImage.ChooseImageDefaultsForMediaType(mediaType);

                    // See if there are other recorded sessions on the disc
                    if (!discFormatData.MediaHeuristicallyBlank)
                    {
                        try
                        {
                            fileSystemImage.MultisessionInterfaces = discFormatData.MultisessionInterfaces;
                            fileSystemImage.ImportFileSystem();
                        }
                        catch (Exception ex)
                        {
                            txtStatus1.Text = GetMediaTypeString(mediaType) + " - " + "Đĩa đã bị khóa chức năng ghi.";
                            Log.WriteLine(ex.ToString());
                            return false;
                        }
                    }

                    Int64 freeMediaBlocks = fileSystemImage.FreeMediaBlocks;
                    long _totalDiscSize = 2048 * freeMediaBlocks;

                    txtStatus1.Text = GetMediaTypeString(mediaType) + " - " + "Dung lượng trống: " + (_totalDiscSize < 1000000000 ?
                        string.Format("{0}MB", _totalDiscSize / 1000000) :
                        string.Format("{0:F2}GB", (float)_totalDiscSize / 1000000000.0));
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine(ex.ToString());
                return false;
            }

            return true;
        }

        private bool detectMedia2()
        {
            if (listDrive2.SelectedIndex == -1)
            {
                txtStatus2.Text = "Không có ổ đĩa";
                return false;
            }

            var discRecorder = (IDiscRecorder2)listDrive2.Items[listDrive2.SelectedIndex];

            MsftFileSystemImage fileSystemImage = null;
            MsftDiscFormat2Data discFormatData = null;

            try
            {
                // Create and initialize the IDiscFormat2Data
                discFormatData = new MsftDiscFormat2Data();
                if (!discFormatData.IsCurrentMediaSupported(discRecorder))
                {
                    txtStatus2.Text = "Ổ đĩa không có chức năng ghi";
                    return false;
                }
                else
                {
                    // Get the media type in the recorder
                    discFormatData.Recorder = discRecorder;
                    IMAPI_MEDIA_PHYSICAL_TYPE mediaType = discFormatData.CurrentPhysicalMediaType;
                    txtStatus2.Text = GetMediaTypeString(mediaType);

                    // Create a file system and select the media type
                    fileSystemImage = new MsftFileSystemImage();
                    fileSystemImage.ChooseImageDefaultsForMediaType(mediaType);

                    // See if there are other recorded sessions on the disc
                    if (!discFormatData.MediaHeuristicallyBlank)
                    {
                        try
                        {
                            fileSystemImage.MultisessionInterfaces = discFormatData.MultisessionInterfaces;
                            fileSystemImage.ImportFileSystem();
                        }
                        catch (Exception ex)
                        {
                            txtStatus2.Text = GetMediaTypeString(mediaType) + " - " + "Đĩa đã bị khóa chức năng ghi.";
                            Log.WriteLine(ex.ToString());
                            return false;
                        }
                    }

                    Int64 freeMediaBlocks = fileSystemImage.FreeMediaBlocks;
                    long _totalDiscSize = 2048 * freeMediaBlocks;

                    txtStatus2.Text = GetMediaTypeString(mediaType) + " - " + "Dung lượng trống: " + (_totalDiscSize < 1000000000 ?
                        string.Format("{0}MB", _totalDiscSize / 1000000) :
                        string.Format("{0:F2}GB", (float)_totalDiscSize / 1000000000.0));
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine(ex.ToString());
                return false;
            }

            return true;

        }

        private void listDrive1_SelectedIndexChanged(object sender, EventArgs e)
        {
            btnWrite1.Enabled = detectMedia1();
            if (listDrive2.SelectedIndex == listDrive1.SelectedIndex)
            {
                txtStatus2.Text = "Hãy chọn ổ đĩa khác";
                btnWrite2.Enabled = false;
            }
        }

        private void listDrive2_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (listDrive2.SelectedIndex == listDrive1.SelectedIndex)
            {
                txtStatus2.Text = "Hãy chọn ổ đĩa khác";
                btnWrite2.Enabled = false;
            }
            else
            {
                btnWrite2.Enabled = detectMedia2();
            }
        }

        private void doAfterBurnWork()
        {
            if (!(_isBurning1 || _isBurning2 || _isBurning3))
            {
                // only clean file if it is already burned to disk
                //HuongND: Keep file in case fail one DVD disk, must complete 2 DVD discs
                //if (_isRecordSuccess1 || _isRecordSuccess2)
                //{
                //    try
                //    {
                //        File.Delete(_filePath);
                //    }
                //    catch (Exception ex)
                //    {
                //        Log.WriteLine(ex.ToString());
                //    }
                //}

                // show new session
                if (MessageBox.Show("Bạn có muốn tạo phiên làm việc mới không?", "Tiếp tục", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    // insert new disk
                    while (true)
                    {
                        if (MessageBox.Show("Hãy đưa đĩa mới vào ổ đĩa và nhấn OK để tiếp tục", "Yêu cầu", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK)
                        {
                            if (CountNewDisk() > 0)
                            {
                                Log.WriteLine(">>> Open new session!");
                                DialogResult = DialogResult.Yes;
                                Close();
                                return;
                            }
                        } // cancel to force close
                        else
                        {
                            Log.WriteLine("Cancel inserting new disk!");
                            DialogResult = DialogResult.Cancel;
                            Close();
                            return;
                        }
                    }
                }
                else
                {
                    Log.WriteLine("Do NOT open new session!");
                    DialogResult = DialogResult.No;
                    Close();
                    return;
                }
            }
        }

        private int CountNewDisk()
        {
            int count = 0;
            foreach (MsftDiscRecorder2 discRecorder in listDrive1.Items)
            {
                if (IsMediaWritable(discRecorder))
                {
                    count++;
                }
            }
            return count;
        }

        private bool IsMediaWritable(MsftDiscRecorder2 discRecorder)
        {
            MsftFileSystemImage fileSystemImage = null;
            MsftDiscFormat2Data discFormatData = null;

            try
            {
                // Create and initialize the IDiscFormat2Data
                discFormatData = new MsftDiscFormat2Data();
                if (!discFormatData.IsCurrentMediaSupported(discRecorder))
                {
                    return false;
                }
                else
                {
                    // Get the media type in the recorder
                    discFormatData.Recorder = discRecorder;
                    IMAPI_MEDIA_PHYSICAL_TYPE mediaType = discFormatData.CurrentPhysicalMediaType;

                    // Create a file system and select the media type
                    fileSystemImage = new MsftFileSystemImage();
                    fileSystemImage.ChooseImageDefaultsForMediaType(mediaType);

                    // See if there are other recorded sessions on the disc
                    if (!discFormatData.MediaHeuristicallyBlank)
                    {
                        try
                        {
                            fileSystemImage.MultisessionInterfaces = discFormatData.MultisessionInterfaces;
                        }
                        catch (Exception ex)
                        {
                            Log.WriteLine(ex.ToString());
                            return false;
                        }
                        fileSystemImage.ImportFileSystem();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine(ex.ToString());
                return false;
            }
            return true;
        }
    }
}