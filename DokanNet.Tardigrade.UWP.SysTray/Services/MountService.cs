﻿using DokanNet.Tardigrade.Contracts.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DokanNet.Tardigrade.UWP.SysTray.Services
{
    class MountService
    {
        private Dictionary<MountParameters, TardigradeMount> _activeMounts;

        public MountService()
        {
            _activeMounts = new Dictionary<MountParameters, TardigradeMount>();
        }

        public void MountAll(List<MountParameters> mountParameters)
        {
            foreach (var mountParameter in mountParameters)
            {
                DokanNet.Tardigrade.TardigradeMount tardigradeMount = new TardigradeMount();

                Task mountTask = Task.Run(() => StartMount(tardigradeMount, mountParameter));
                _activeMounts.Add(mountParameter, tardigradeMount);
            }
        }

        public void UnmountAll()
        {
            foreach (var entry in _activeMounts.ToList())
            {
                entry.Value.Unmount();
                _activeMounts.Remove(entry.Key);
            }
        }

        public bool GetDrivesMounted()
        {
            return _activeMounts.Count() > 0;
        }

        private void StartMount(TardigradeMount mount, MountParameters mountParameters)
        {
            try
            {
                mount.MountAsync(mountParameters).Wait();
            }
            catch (Exception ex)
            {
                var error = Properties.Resources.MountError.Replace("$error$", ex.Message).Replace("$stack$", ex.StackTrace);

                MessageBox.Show(error);
            }
        }
    }
}
