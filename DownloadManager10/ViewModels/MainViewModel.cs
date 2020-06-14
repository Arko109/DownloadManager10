using DownloadManager10.Helpers;
using DownloadManager10.Models;
using DownloadManager10.Services;
using Microsoft.Toolkit.Uwp.Notifications;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Networking.BackgroundTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Notifications;
using Windows.UI.Xaml;

namespace DownloadManager10.ViewModels
{
    public class MainViewModel : Observable
    {
        private readonly HttpClient _httpClient = new HttpClient();
        private readonly Regex _regex = new Regex(@"[^\/?#]*\.[^\/?#]*");

        public ObservableCollection<DownloadItem> Downloads = new ObservableCollection<DownloadItem>();
        private string _url;

        public string Url
        {
            get => _url;
            set { Set(ref _url, value); }
        }

        private BackgroundTransferPriority _priority;

        public BackgroundTransferPriority Priority
        {
            get => _priority;
            set { Set(ref _priority, value); }
        }

        public List<BackgroundTransferPriority> Priorities = new List<BackgroundTransferPriority>()
        {
            BackgroundTransferPriority.Default,
            BackgroundTransferPriority.High,
            BackgroundTransferPriority.Low
        };

        #region Commands

        private RelayCommand _downloadCommand;

        public RelayCommand DownloadCommand => _downloadCommand ??= new RelayCommand(() =>
        {
            StartDownload();
        });

        private RelayCommand _onNavigatedTo;

        public RelayCommand OnNavigatedTo => _onNavigatedTo ??= new RelayCommand(async () =>
        {
            await DiscoverActiveDownloadsAsync();
        });

        private RelayCommand _cancelAllCommand;

        public RelayCommand CancelAllCommand => _cancelAllCommand ??= new RelayCommand(() =>
        {
            Debug.WriteLine("Canceling Downloads: " + Downloads.Count);

            foreach (var dl in Downloads.Where(di => !di.CancellationTokenSource.IsCancellationRequested))
            {
                dl.CancellationTokenSource.Cancel();
                dl.CancellationTokenSource.Dispose();
            }

            Downloads.Clear();
        });

        private RelayCommand _pauseAllCommand;

        public RelayCommand PauseAllCommand => _pauseAllCommand ??= new RelayCommand(() =>
        {
            foreach (var dl in Downloads.Where(di => di.Status == BackgroundTransferStatus.Running))
            {
                dl.DownloadOperation.Pause();
            }
        });

        private RelayCommand _resumeAllCommand;

        public RelayCommand ResumeAllCommand => _resumeAllCommand ??= new RelayCommand(() =>
        {
            foreach (var dl in Downloads.Where(di => di.Status == BackgroundTransferStatus.PausedByApplication || di.Status == BackgroundTransferStatus.PausedCostedNetwork || di.Status == BackgroundTransferStatus.PausedNoNetwork || di.Status == BackgroundTransferStatus.PausedRecoverableWebErrorStatus || di.Status == BackgroundTransferStatus.PausedSystemPolicy))
            {
                dl.DownloadOperation.Resume();
            }
        });

        #endregion Commands

        public MainViewModel()
        {
            VersionDescription = GetVersionDescription();
        }

        private async Task DiscoverActiveDownloadsAsync()
        {
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

        private async void StartDownload()
        {
            if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out Uri source))
                return;

            string fileName = null;
            var response = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, source), HttpCompletionOption.ResponseHeadersRead);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentDisposition is ContentDispositionHeaderValue cd)
                fileName = cd.FileName;
            if (fileName == null)
            {
                MatchCollection matches = _regex.Matches(source.LocalPath);
                fileName = matches.LastOrDefault()?.Value;
            }
            fileName ??= "download.bin";

            StorageFile destinationFile;
            FileSavePicker savePicker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            var ext = fileName.Split('.').LastOrDefault() ?? "bin";
            savePicker.FileTypeChoices.Add(ext, new List<string>() { '.' + ext });
            savePicker.SuggestedFileName = fileName;

            destinationFile = await savePicker.PickSaveFileAsync();
            if (destinationFile == null)
                return;

            BackgroundDownloader downloader = new BackgroundDownloader();
            ToastContent content = new ToastContent()
            {
                Visual = new ToastVisual()
                {
                    BindingGeneric = new ToastBindingGeneric()
                    {
                        Children =
                        {
                            new AdaptiveText()
                            {
                                Text = fileName
                            },
                            new AdaptiveText()
                            {
                                Text = "Download failed"
                            }
                        }
                    }
                }
            };
            downloader.FailureToastNotification = new ToastNotification(content.GetXml());
            content = new ToastContent()
            {
                Visual = new ToastVisual()
                {
                    BindingGeneric = new ToastBindingGeneric()
                    {
                        Children =
                        {
                            new AdaptiveText()
                            {
                                Text = fileName
                            },
                            new AdaptiveText()
                            {
                                Text = "Download succeded"
                            }
                        }
                    }
                }
            };
            downloader.SuccessToastNotification = new ToastNotification(content.GetXml());

            DownloadOperation download = downloader.CreateDownload(source, destinationFile);

            Debug.WriteLine(string.Format(CultureInfo.CurrentCulture, "Downloading {0} to {1} with {2} priority, {3}",
                source.AbsoluteUri, destinationFile.Name, Priority, download.Guid));

            download.Priority = Priority;

            await HandleDownloadAsync(download, true);
            Url = string.Empty;
            Priority = BackgroundTransferPriority.Default;
        }

        private async Task HandleDownloadAsync(DownloadOperation download, bool start)
        {
            DownloadItem downloadItem = new DownloadItem()
            {
                Guid = download.Guid,
                Name = download.ResultFile.Name,
                DownloadOperation = download,
                CancellationTokenSource = new CancellationTokenSource()
            };
            try
            {
                Debug.WriteLine("Running: " + download.Guid);

                Downloads.Add(downloadItem);

                Progress<DownloadOperation> progressCallback = new Progress<DownloadOperation>(DownloadProgress);
                if (start)
                {
                    await download.StartAsync().AsTask(downloadItem.CancellationTokenSource.Token, progressCallback);
                }
                else
                {
                    await download.AttachAsync().AsTask(downloadItem.CancellationTokenSource.Token, progressCallback);
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
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
            finally
            {
                downloadItem.Status = downloadItem.DownloadOperation.Progress.Status;
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
                di.Status = currentProgress.Status;
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

        public RelayCommand<ElementTheme> SwitchThemeCommand => _switchThemeCommand ??= new RelayCommand<ElementTheme>(async (param) =>
        {
            await ThemeSelectorService.SetThemeAsync(ElementTheme = param);
        });

        private string GetVersionDescription()
        {
            var appName = "AppDisplayName".GetLocalized();
            var version = Package.Current.Id.Version;

            return $"{appName} - {version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }

        #endregion Settings flyout
    }
}
