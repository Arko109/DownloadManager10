using DownloadManager10.Helpers;
using System;

namespace DownloadManager10.Models
{
    public class DownloadItem : Observable
    {
        private string _name;

        public string Name
        {
            get => _name;
            set { Set(ref _name, value); }
        }

        private double _progress;

        public double Progress
        {
            get { return _progress; }
            set { Set(ref _progress, value); }
        }

        private Guid _guid;

        public Guid Guid
        {
            get => _guid;
            set { Set(ref _guid, value); }
        }

        private string _status;

        public string Status
        {
            get => _status;
            set { Set(ref _status, value); }
        }
    }
}