﻿using DokanNet.Tardigrade.Contracts.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.UI.Xaml;

namespace DokanNet.Tardigrade.UWP.ViewModels
{
    public class MountViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<MountParameterViewModel> Mounts { get; set; }

        private bool _mountsActive = true;

        public event PropertyChangedEventHandler PropertyChanged;

        public bool MountsActive
        {
            get
            {
                return _mountsActive;
            }
            set
            {
                _mountsActive = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MountsActive)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartDrivesVisibility)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StopDrivesVisibility)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MountsInactive)));
            }
        }

        private bool _isDokanyInstalled = false;
        public bool IsDokanyInstalled
        {
            get
            {
                return _isDokanyInstalled;
            }
            set
            {
                _isDokanyInstalled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDokanyInstalled)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DokanyIsMissingInfoVisibility)));
            }
        }

        public Visibility StartDrivesVisibility
        {
            get
            {
                return !_mountsActive ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public Visibility StopDrivesVisibility
        {
            get
            {
                return _mountsActive ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public Visibility DokanyIsMissingInfoVisibility
        {
            get
            {
                return _isDokanyInstalled ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        public bool MountsInactive
        {
            get
            {
                return !_mountsActive;
            }
        }

        public MountViewModel()
        {
            Mounts = new ObservableCollection<MountParameterViewModel>();
        }
    }
}
