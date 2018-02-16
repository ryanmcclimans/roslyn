﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Composition.Hosting;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.CodeAnalysis.Editor;
using Microsoft.CodeAnalysis.Editor.Implementation.ForegroundNotification;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Remote;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.VisualStudio.Composition;
using Roslyn.Test.Utilities;
using Xunit.Sdk;

namespace Microsoft.CodeAnalysis.Test.Utilities
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class UseExportProviderAttribute : BeforeAfterTestAttribute
    {
        private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);
        private readonly object _remoteHostServicesCreationLock = new object();
        private MefHostServices _hostServices;
        private MefHostServices _remoteHostServices;

        public override void Before(MethodInfo methodUnderTest)
        {
            MefHostServices.HookServiceCreation(CreateMefHostServices);
            RoslynServices.HookHostServices(GetOrCreateRemoteHostServices);
            DesktopMefHostServices.ResetHostServicesTestOnly();

            // make sure we enable this for all unit tests
            AsynchronousOperationListenerProvider.Enable(true);
            ExportProviderCache.EnabledViaUseExportProviderAttributeOnly = true;
        }

        public override void After(MethodInfo methodUnderTest)
        {
            var exportProvider = ExportProviderCache.ExportProviderForCleanup;
            try
            {
                var listenerProvider = exportProvider?.GetExportedValues<IAsynchronousOperationListenerProvider>().SingleOrDefault();
                if (listenerProvider != null)
                {
                    var stopwatch = Stopwatch.StartNew();

                    var foregroundNotificationService = exportProvider?.GetExportedValues<IForegroundNotificationService>().SingleOrDefault() as ForegroundNotificationService;
                    foregroundNotificationService?.ReleaseCancelledItems();

                    var waiter = ((AsynchronousOperationListenerProvider)listenerProvider).WaitAllDispatcherOperationAndTasksAsync();
                    var timeoutTokenSource = new CancellationTokenSource(CleanupTimeout);
                    Func<CancellationToken, Task<bool>> continueUntilAsync =
                        async cancellationToken =>
                        {
                            await Task.WhenAny(waiter, Task.Delay(CleanupTimeout, cancellationToken)).ConfigureAwait(false);
                            return false;
                        };
                    while (!waiter.IsCompleted)
                    {
                        if (stopwatch.Elapsed >= CleanupTimeout)
                        {
                            throw new XunitException("Failed to clean up listeners in a timely manner.");
                        }

                        Dispatcher.CurrentDispatcher.DoEvents(continueUntilAsync, timeoutTokenSource.Token);
                    }

                    waiter.GetAwaiter().GetResult();
                }
            }
            finally
            {
                exportProvider?.Dispose();
                MefHostServices.HookServiceCreation((_, __) => throw new InvalidOperationException("Cannot create host services after test tear down."));
                RoslynServices.HookHostServices(() => throw new InvalidOperationException("Cannot create host services after test tear down."));
                DesktopMefHostServices.ResetHostServicesTestOnly();
                _hostServices = null;
                ExportProviderCache.EnabledViaUseExportProviderAttributeOnly = false;
            }
        }

        private MefHostServices CreateMefHostServices(IEnumerable<Assembly> assemblies, bool requestingDefaultAssemblies)
        {
            if (requestingDefaultAssemblies && ExportProviderCache.ExportProviderForCleanup != null)
            {
                if (_hostServices == null)
                {
                    var hostServices = new ExportProviderMefHostServices(ExportProviderCache.ExportProviderForCleanup);
                    Interlocked.CompareExchange(ref _hostServices, hostServices, null);
                }

                return _hostServices;
            }

            var catalog = ExportProviderCache.CreateAssemblyCatalog(assemblies);
            Interlocked.CompareExchange(
                ref _hostServices,
                new ExportProviderMefHostServices(ExportProviderCache.CreateExportProvider(catalog)),
                null);

            return _hostServices;
        }

        private HostServices GetOrCreateRemoteHostServices()
        {
            if (_remoteHostServices != null)
            {
                return _remoteHostServices;
            }

            lock (_remoteHostServicesCreationLock)
            {
                if (_remoteHostServices == null)
                {
                    var compositionConfiguration = new ContainerConfiguration().WithAssemblies(RoslynServices.RemoteHostAssemblies.Distinct());
                    var container = compositionConfiguration.CreateContainer();
                    _remoteHostServices = new MefHostServices(container);
                }
            }

            return _remoteHostServices;
        }

        private class ExportProviderMefHostServices : MefHostServices, IMefHostExportProvider
        {
            private readonly MefV1HostServices _mefV1HostServices;

            public ExportProviderMefHostServices(ExportProvider exportProvider)
                : base(new ContainerConfiguration().CreateContainer())
            {
                _mefV1HostServices = MefV1HostServices.Create(exportProvider.AsExportProvider());
            }

            protected internal override HostWorkspaceServices CreateWorkspaceServices(Workspace workspace)
            {
                return _mefV1HostServices.CreateWorkspaceServices(workspace);
            }

            IEnumerable<Lazy<TExtension, TMetadata>> IMefHostExportProvider.GetExports<TExtension, TMetadata>()
            {
                return _mefV1HostServices.GetExports<TExtension, TMetadata>();
            }

            IEnumerable<Lazy<TExtension>> IMefHostExportProvider.GetExports<TExtension>()
            {
                return _mefV1HostServices.GetExports<TExtension>();
            }
        }
    }
}
