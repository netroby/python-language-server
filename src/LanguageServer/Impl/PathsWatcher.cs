// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.PythonTools.Analysis.Infrastructure;

namespace Microsoft.Python.LanguageServer {
    internal sealed class PathsWatcher : IDisposable {
        private readonly DisposableBag _disposableBag = new DisposableBag(nameof(PathsWatcher));
        private readonly Action _onChanged;
        private readonly object _lock = new object();
        private readonly ILogger _log;

        private Timer _throttleTimer;
        private bool _changedSinceLastTick;

        public PathsWatcher(string[] paths, Action onChanged, ILogger log) {
            _log = log;
            paths = paths != null ? paths.Where(p => Path.IsPathRooted(p)).ToArray() : Array.Empty<string>();
            if (paths.Length == 0) {
                return;
            }

            _onChanged = onChanged;

            foreach (var p in paths) {
                try {
                    if (!Directory.Exists(p)) {
                        continue;
                    }
                } catch (IOException ex) {
                    _log.TraceMessage($"Unable to access directory {p}, exception {ex.Message}");
                    continue;
                }

                try {
                    var fsw = new System.IO.FileSystemWatcher(p) {
                        IncludeSubdirectories = true,
                        EnableRaisingEvents = true,
                        NotifyFilter = NotifyFilters.FileName
                    };

                    fsw.Created += OnChanged;
                    fsw.Deleted += OnChanged;

                    _disposableBag
                        .Add(() => _throttleTimer?.Dispose())
                        .Add(() => fsw.Created -= OnChanged)
                        .Add(() => fsw.Deleted -= OnChanged)
                        .Add(() => fsw.EnableRaisingEvents = false)
                        .Add(fsw);
                } catch (ArgumentException ex) {
                    _log.TraceMessage($"Unable to create file watcher for {p}, exception {ex.Message}");
                }
            }
        }

        private void OnChanged(object sender, FileSystemEventArgs e) {
            // Throttle calls so we don't get flooded with requests
            // if there is massive change to the file structure.
            lock (_lock) {
                if ((e.ChangeType & WatcherChangeTypes.Created) == WatcherChangeTypes.Created ||
                    (e.ChangeType & WatcherChangeTypes.Deleted) == WatcherChangeTypes.Deleted) {
                    // Mark as change since last timer tick. We only want to call reload when
                    // all the files and directories changes are complete.
                    _changedSinceLastTick = true;
                    _throttleTimer = _throttleTimer ?? new Timer(TimerProc, null, 1000, 1000);
                }
            }
        }

        private void TimerProc(object o) {
            lock (_lock) {
                // Check if there were no changes since the last tick.
                // We only want to perform action when all file change 
                // activities ceased.
                if (!_changedSinceLastTick) {
                    ThreadPool.QueueUserWorkItem(_ => _onChanged());
                    _throttleTimer?.Dispose();
                    _throttleTimer = null;
                }
                _changedSinceLastTick = false;
            }
        }
        public void Dispose() => _disposableBag.TryDispose();
    }
}
