﻿using DownloadManager10.Helpers;
using DownloadManager10.Models;
using DownloadManager10.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Networking.BackgroundTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;

namespace DownloadManager10.ViewModels
{
    public class MainViewModel : Observable
    {
        private ObservableCollection<DownloadItem> _downloads;
        public ObservableCollection<DownloadItem> Downloads => _downloads ?? (_downloads = new ObservableCollection<DownloadItem>());
        public string Url;
        public BackgroundTransferPriority Priority;

        public List<BackgroundTransferPriority> Priorities = new List<BackgroundTransferPriority>()
        {
            BackgroundTransferPriority.Default,
            BackgroundTransferPriority.High,
            BackgroundTransferPriority.Low
        };

        private List<DownloadOperation> activeDownloads;
        private CancellationTokenSource cts;

        private RelayCommand _downloadCommand;

        public RelayCommand DownloadCommand => _downloadCommand ?? (_downloadCommand = new RelayCommand(() =>
        {
            StartDownload(BackgroundTransferPriority.Default);
        }));

        private RelayCommand _onNavigatedTo;

        public RelayCommand OnNavigatedTo => _onNavigatedTo ?? (_onNavigatedTo = new RelayCommand(async () =>
        {
            await DiscoverActiveDownloadsAsync();
        }));

        private RelayCommand _cancelAllCommand;

        public RelayCommand CancelAllCommand => _cancelAllCommand ?? (_cancelAllCommand = new RelayCommand(() =>
        {
            Debug.WriteLine("Canceling Downloads: " + activeDownloads.Count);

            cts.Cancel();
            cts.Dispose();

            cts = new CancellationTokenSource();
            activeDownloads = new List<DownloadOperation>();
        }));

        public MainViewModel()
        {
            cts = new CancellationTokenSource();
            VersionDescription = GetVersionDescription();
        }

        private async Task DiscoverActiveDownloadsAsync()
        {
            activeDownloads = new List<DownloadOperation>();

            IReadOnlyList<DownloadOperation> downloads;
            try
            {
                downloads = await BackgroundDownloader.GetCurrentDownloadsAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                return;
            }

            Debug.WriteLine("Loading background downloads: " + downloads.Count);
            Downloads.Clear();
            if (downloads.Count > 0)
            {
                List<Task> tasks = new List<Task>();
                foreach (DownloadOperation download in downloads)
                {
                    Debug.WriteLine(String.Format(CultureInfo.CurrentCulture,
                        "Discovered background download: {0}, Status: {1}", download.Guid,
                        download.Progress.Status));

                    tasks.Add(HandleDownloadAsync(download, false));
                }

                await Task.WhenAll(tasks);
            }
        }

        private async void StartDownload(BackgroundTransferPriority priority)
        {
            if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out Uri source))
                return;

            string fileName;
            Regex regex = new Regex(@"[^\/?#]*\.[^\/?#]*");
            MatchCollection matches = regex.Matches(source.LocalPath);
            fileName = matches.LastOrDefault()?.Value ?? "download.txt";

            StorageFile destinationFile;
            FileSavePicker savePicker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            var ext = fileName.Split('.').LastOrDefault() ?? "txt";
            savePicker.FileTypeChoices.Add(ext, new List<string>() { '.' + ext });
            savePicker.SuggestedFileName = fileName;

            destinationFile = await savePicker.PickSaveFileAsync();
            if (destinationFile == null)
                return;

            BackgroundDownloader downloader = new BackgroundDownloader();
            DownloadOperation download = downloader.CreateDownload(source, destinationFile);

            Debug.WriteLine(String.Format(CultureInfo.CurrentCulture, "Downloading {0} to {1} with {2} priority, {3}",
                source.AbsoluteUri, destinationFile.Name, priority, download.Guid));

            download.Priority = priority;

            await HandleDownloadAsync(download, true);
        }

        private async Task HandleDownloadAsync(DownloadOperation download, bool start)
        {
            try
            {
                Debug.WriteLine("Running: " + download.Guid);

                activeDownloads.Add(download);
                Downloads.Add(new DownloadItem() { Guid = download.Guid, Name = download.ResultFile.Name });

                Progress<DownloadOperation> progressCallback = new Progress<DownloadOperation>(DownloadProgress);
                if (start)
                {
                    await download.StartAsync().AsTask(cts.Token, progressCallback);
                }
                else
                {
                    await download.AttachAsync().AsTask(cts.Token, progressCallback);
                }

                ResponseInformation response = download.GetResponseInformation();

                string statusCode = response != null ? response.StatusCode.ToString() : String.Empty;

                Debug.WriteLine(
                    String.Format(
                        CultureInfo.CurrentCulture,
                        "Completed: {0}, Status Code: {1}",
                        download.Guid,
                        statusCode));
            }
            catch (TaskCanceledException)
            {
                Debug.WriteLine("Canceled: " + download.Guid);
                Downloads.FirstOrDefault(d => d.Guid.Equals(download.Guid)).Status = "Cancelled";
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
            finally
            {
                activeDownloads.Remove(download);
                if (Downloads.FirstOrDefault(d => d.Guid.Equals(download.Guid)) is DownloadItem di && di.Status != "Cancelled")
                    di.Status = "Finished";
            }
        }

        private void DownloadProgress(DownloadOperation download)
        {
            BackgroundDownloadProgress currentProgress = download.Progress;
            double percent = 100;
            if (currentProgress.TotalBytesToReceive > 0)
            {
                percent = currentProgress.BytesReceived * 100d / currentProgress.TotalBytesToReceive;
            }

            if (Downloads.FirstOrDefault(d => d.Guid.Equals(download.Guid)) is DownloadItem di)
            {
                di.Progress = percent;
                di.Status = currentProgress.Status.ToString();
            }
        }

        #region Settings flyout

        private ElementTheme _elementTheme = ThemeSelectorService.Theme;

        public ElementTheme ElementTheme
        {
            get { return _elementTheme; }

            set { Set(ref _elementTheme, value); }
        }

        private string _versionDescription;

        public string VersionDescription
        {
            get { return _versionDescription; }

            set { Set(ref _versionDescription, value); }
        }

        private RelayCommand<ElementTheme> _switchThemeCommand;

        public RelayCommand<ElementTheme> SwitchThemeCommand
        {
            get
            {
                if (_switchThemeCommand == null)
                {
                    _switchThemeCommand = new RelayCommand<ElementTheme>(
                        async (param) =>
                        {
                            ElementTheme = param;
                            await ThemeSelectorService.SetThemeAsync(param);
                        });
                }

                return _switchThemeCommand;
            }
        }

        private string GetVersionDescription()
        {
            var appName = "AppDisplayName".GetLocalized();
            var package = Package.Current;
            var packageId = package.Id;
            var version = packageId.Version;

            return $"{appName} - {version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }

        #endregion Settings flyout
    }
}