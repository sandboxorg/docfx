﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.Engine
{
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.IO;
    using System.Linq;

    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.Plugins;

    internal class CompilePhaseHandler : IPhaseHandler
    {
        private readonly List<RestructureTableOfContent> _delegates = new List<RestructureTableOfContent>();

        public string Name => nameof(CompilePhaseHandler);

        public BuildPhase Phase => BuildPhase.Compile;

        public DocumentBuildContext Context { get; }

        public CompilePhaseHandler(DocumentBuildContext context)
        {
            Context = context;
        }

        public void Handle(List<HostService> hostServices, int maxParallelism)
        {
            Prepare(hostServices);
            foreach (var hostService in hostServices)
            {
                using (new LoggerPhaseScope(hostService.Processor.Name, LogLevel.Verbose))
                {
                    var steps = string.Join("=>", hostService.Processor.BuildSteps.OrderBy(step => step.BuildOrder).Select(s => s.Name));
                    Logger.LogInfo($"Building {hostService.Models.Count} file(s) in {hostService.Processor.Name}({steps})...");
                    Logger.LogVerbose($"Processor {hostService.Processor.Name}: Prebuilding...");
                    using (new LoggerPhaseScope("Prebuild", LogLevel.Verbose))
                    {
                        Prebuild(hostService);
                    }

                    // Register all the delegates to handler
                    if (hostService.RestructureTableOfContentDelegates != null)
                    {
                        _delegates.AddRange(hostService.RestructureTableOfContentDelegates);
                    }
                }
            }

            DistributeTocDelegates(hostServices);

            foreach (var hostService in hostServices)
            {
                using (new LoggerPhaseScope(hostService.Processor.Name, LogLevel.Verbose))
                {
                    Logger.LogVerbose($"Processor {hostService.Processor.Name}: Building...");
                    using (new LoggerPhaseScope("Build", LogLevel.Verbose))
                    {
                        BuildArticle(hostService, maxParallelism);
                    }
                }
            }
        }

        #region Private Methods

        private void Prepare(List<HostService> hostServices)
        {
            if (Context == null)
            {
                return;
            }
            foreach (var hostService in hostServices)
            {
                hostService.SourceFiles = Context.AllSourceFiles;
                foreach (var m in hostService.Models)
                {
                    if (m.LocalPathFromRepoRoot == null)
                    {
                        m.LocalPathFromRepoRoot = StringExtension.ToDisplayPath(Path.Combine(m.BaseDir, m.File));
                    }
                    if (m.LocalPathFromRoot == null)
                    {
                        m.LocalPathFromRoot = StringExtension.ToDisplayPath(Path.Combine(m.BaseDir, m.File));
                    }
                }
            }
        }

        private void DistributeTocDelegates(List<HostService> hostServices)
        {
            if (_delegates.Count > 0)
            {
                var delegates = _delegates.ToImmutableArray();
                // Distribute delegates to all the hostServices
                foreach (var hostService in hostServices)
                {
                    hostService.RestructureTableOfContentDelegates = delegates;
                }
            }
        }

        private static void Prebuild(HostService hostService)
        {
            BuildPhaseUtility.RunBuildSteps(
                hostService.Processor.BuildSteps,
                buildStep =>
                {
                    Logger.LogVerbose($"Processor {hostService.Processor.Name}, step {buildStep.Name}: Prebuilding...");
                    using (new LoggerPhaseScope(buildStep.Name, LogLevel.Verbose))
                    {
                        var models = buildStep.Prebuild(hostService.Models, hostService);
                        if (!object.ReferenceEquals(models, hostService.Models))
                        {
                            Logger.LogVerbose($"Processor {hostService.Processor.Name}, step {buildStep.Name}: Reloading models...");
                            hostService.Reload(models);
                        }
                    }
                });
        }

        private static void BuildArticle(HostService hostService, int maxParallelism)
        {
            hostService.Models.RunAll(
                m =>
                {
                    using (new LoggerFileScope(m.LocalPathFromRoot))
                    {
                        Logger.LogDiagnostic($"Processor {hostService.Processor.Name}: Building...");
                        BuildPhaseUtility.RunBuildSteps(
                            hostService.Processor.BuildSteps,
                            buildStep =>
                            {
                                Logger.LogDiagnostic($"Processor {hostService.Processor.Name}, step {buildStep.Name}: Building...");
                                using (new LoggerPhaseScope(buildStep.Name, LogLevel.Diagnostic))
                                {
                                    buildStep.Build(m, hostService);
                                }
                            });
                    }
                },
                maxParallelism);
        }

        #endregion
    }
}
