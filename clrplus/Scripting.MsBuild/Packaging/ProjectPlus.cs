//-----------------------------------------------------------------------
// <copyright company="CoApp Project">
//     Copyright (c) 2010-2013 Garrett Serack and CoApp Contributors. 
//     Contributors can be discovered using the 'git log' command.
//     All rights reserved.
// </copyright>
// <license>
//     The software is licensed under the Apache 2.0 License (the "License")
//     You may not use the software except in compliance with the License. 
// </license>
//-----------------------------------------------------------------------

namespace ClrPlus.Scripting.MsBuild.Packaging {
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using Core.Collections;
    using Core.Extensions;
    using Core.Tasks;
    using Languages.PropertySheetV3.Mapping;
    using Microsoft.Build.Construction;
    using Microsoft.Build.Evaluation;
    using Platform;
    using Utility;
    

    public class ProjectPlus : Project, IDisposable {
        private readonly IProjectOwner _owner;
        internal List<string> Conditions = new List<string>();

        internal StringPropertyList InitialTargets;
        private Dictionary<ProjectTargetElement, ListWithAction<string>> _copyToTargets;
        private Dictionary<ProjectTargetElement, ListWithAction<string>> _rlCheck;
        private Dictionary<ProjectItemGroupElement, ListWithAction<string>> _embedOutputs;
        private Dictionary<string, ListWithAction<string>> _buildTimePaths;
        private HashSet<string> _copyToTargetsDuplicateCheck = new HashSet<string>();

        public Core.Utility.LazyEx<ProjectTargetElement> EarlyInitTarget;
        public Core.Utility.LazyEx<ProjectTargetElement> FirstInitTarget;
        public Core.Utility.LazyEx<ProjectTargetElement> SecondInitTarget;
        public Core.Utility.LazyEx<ProjectTargetElement> ItemGroupInitTarget;

       
        public ProjectPlus(IProjectOwner owner, string filename) {
            _owner = owner;
            FullPath = Path.Combine(_owner.Directory, filename);
            InitialTargets = new StringPropertyList(() => Xml.InitialTargets, v => Xml.InitialTargets = v, target => LookupTarget(target, null), true);

            FirstInitTarget = new Core.Utility.LazyEx<ProjectTargetElement>(() => {
                var tgt = LookupTarget("{0}_init_{1}_1".format(SafeName, Path.GetExtension(Filename).Trim('.')), null, true);
                InitialTargets.Add(tgt.Name);
                return tgt;
            });

            SecondInitTarget = new Core.Utility.LazyEx<ProjectTargetElement>(() => {
                // ensure the first one is created.
                var x = FirstInitTarget.Value;

                var tgt = LookupTarget("{0}_init_{1}_2".format(SafeName, Path.GetExtension(Filename).Trim('.')), null, true);
                InitialTargets.Add(tgt.Name);
                return tgt;
            });

            ItemGroupInitTarget = new Core.Utility.LazyEx<ProjectTargetElement>(() => {
                // ensure the first one is created.
                var x = SecondInitTarget.Value;

                var tgt = LookupTarget("{0}_ItemGroupInit_{1}_3".format(SafeName, Path.GetExtension(Filename).Trim('.')), null, true);
                InitialTargets.Add(tgt.Name);
                return tgt;
            });

            EarlyInitTarget = new Core.Utility.LazyEx<ProjectTargetElement>(() => {
                var tgt = LookupTarget("{0}_init_{1}_0".format(SafeName, Path.GetExtension(Filename).Trim('.')), null, true);
                InitialTargets.Insert(0,tgt.Name);
                return tgt;
            });
        }

        internal string Name {
            get {
                return _owner.ProjectName;
            }
        }

        internal string SafeName {
            get {
                return Name.MakeSafeFileName().Replace(".", "_");
            }
        }

        internal Pivots Pivots {
            get {
                return _owner.Pivots;
            }
        }

        public string Filename {
            get {
                return Path.GetFileName(FullPath);
            }
        }

        public IEnumerable<ToRoute> MemberRoutes {
            get {
                yield return "InitialTargets".MapTo(InitialTargets);

                yield return "ItemDefinitionGroup".MapTo(() => LookupItemDefinitionGroup(""), ItemDefinitionGroupChildren);
                // yield return "CopyToOutput".MapTo(() => CopyToOutput(""));
                yield return "ExpectedRuntimeLibrary".MapTo(() => CheckRuntimeLibrary(""));
                // yield return "EmbedInOutput".MapTo(() => EmbedInOutput(""));
                yield return "ImportGroup".MapTo(() => LookupImportGroup(""), ImportGroupChildren);

                yield return "ItemGroup".MapTo(() => LookupItemGroup(""), ItemGroupChildren);
                yield return "PropertyGroup".MapTo(() => LookupPropertyGroup(""), PropertyGroupChildren);

                yield return "Target".MapTo(new DelegateDictionary<string, ProjectTargetElement>(
                    () => Targets.Keys,
                    key => LookupTarget(key, ""),
                    (name, value) => LookupTarget(name, ""),
                    key => Targets.Remove(key)
                    ), new[] { "$$INDEXED".MapIndexedChildrenTo<ProjectTargetElement>((target, child) => target.GetTargetItem(child)) });

                // and for ones requiring a condition parameter
                yield return "condition".MapTo(() => ConditionCreate(), key => Pivots.NormalizeExpression(key), ConditionRoutes());

                yield return         "*".MapTo(() => ConditionCreate(), key => Pivots.NormalizeExpression(key), ConditionRoutes());

                yield return "".MapTo( (view) => {
                    if (view.HasChildren) {
                        return new Accessor(() => (object)LookupTarget(view.MemberName), v => {});
                    } else 
                        return new Accessor(() => {
                            if (!view.HasChildren) {
                                return (object)LookupProperty(LookupPropertyGroup(""), view.MemberName).Value;
                            }
                            return "";
                        }, v => {
                            if (v != null || v.ToString() != string.Empty) {
                                LookupProperty(LookupPropertyGroup(""), view.MemberName).Value = v.ToString();
                            }
                        });
                    //} else {
                        //return new Accessor(() => LookupTarget(view.MemberName), v => {});
                    //}
                }, new[] { "$$INDEXED".MapIndexedChildrenTo<object>((targetName, child) => LookupTarget(child.ParentView.ParentView.MemberName).GetTargetItem(child)) });
            }
        }

        internal IEnumerable<ToRoute> ConditionRoutes() {
            yield return "ItemDefinitionGroup".MapTo<string>(condition => LookupItemDefinitionGroup(condition), ItemDefinitionGroupChildren);
            // yield return "CopyToOutput".MapTo<string>(condition => CopyToOutput(condition));
            yield return "ExpectedRuntimeLibrary".MapTo<string>(condition => CheckRuntimeLibrary(condition));
            // yield return "EmbedInOutput".MapTo<string>(condition => EmbedInOutput(condition));
            yield return "BuildTimePath".MapTo<string>(condition => BuildTimePath(condition));
            yield return "ImportGroup".MapTo<string>(condition => LookupImportGroup(condition), ImportGroupChildren);
            yield return "ItemGroup".MapTo<string>(condition => LookupItemGroup(condition), ItemGroupChildren);
            yield return "PropertyGroup".MapTo<string>(condition => LookupPropertyGroup(condition), PropertyGroupChildren);

            yield return "Target".MapTo<string, string, ProjectTargetElement>(condition => new DelegateDictionary<string, ProjectTargetElement>(
                () => Targets.Keys,
                key => LookupTarget(key, condition),
                (name, value) => LookupTarget(name, ""),
                key => Targets.Remove(key)
                ), new[] { "$$INDEXED".MapIndexedChildrenTo<ProjectTargetElement>((target, child) => target.GetTargetItem(child)) });


            yield return "".MapTo<string>((condition,view) => {

                return new Accessor(() => {
                    return LookupProperty(  LookupPropertyGroup(condition), view.MemberName).Value;
                }, v => {
                    if (v != null || v.ToString() != string.Empty) {
                        LookupProperty(LookupPropertyGroup(condition), view.MemberName).Value = v.ToString();
                    }
                });

            }, new[] { "$$INDEXED".MapIndexedChildrenTo<string>((targetName, child) => LookupTarget(child.ParentView.ParentView.MemberName).GetTargetItem(child)) });
        }

     

        private IEnumerable<ToRoute> ImportGroupChildren {
            get {
                yield break;
            }
        }

        private IEnumerable<ToRoute> ItemGroupChildren {
            get {
                // treat DC's as "<None ><DeploymentContent>true</DeploymentContent></None>"
                yield return "DeploymentContent".MapTo<ProjectItemGroupElement>((parent, view) => {
                    return new ListWithAction<string>(s => {
                        // when an item is added
                        var item = parent.AddItem("None", s);
                        item.AddMetadata("DeploymentContent", "true");
                    });
                });

                yield return "".MapTo<ProjectItemGroupElement>((parent, view) => {
                    return  new ListWithAction<string>(s => {
                        // when an item is added
                        parent.AddItem(view.MemberName, s);
                    });
                });
            }
        }

        private IEnumerable<ToRoute> PropertyGroupChildren {
            get {
                yield return "".MapTo<ProjectPropertyGroupElement>((parent, view) => {
                    var prop = LookupProperty(parent, view.MemberName);
                    return new Accessor(() => {
                        return prop.Value;
                    }, v => {
                        prop.Value = v.ToString();
                    });

                });
            }
        }

        private static IEnumerable<ToRoute> ItemDefinitionGroupChildren {
            get {
                yield return "PostBuildEvent".ItemDefinitionRoute();
                yield return "Midl".ItemDefinitionRoute(MidlChildren);
                yield return "ResourceCompile".ItemDefinitionRoute(ResourceCompileChildren);
                yield return "BcsMake".ItemDefinitionRoute();
                yield return "ClCompile".ItemDefinitionRoute(ClCompileChildren);
                yield return "Lib".ItemDefinitionRoute(LibChildren);
                yield return "Link".ItemDefinitionRoute(LinkChildren);
            }
        }

        private static IEnumerable<ToRoute> LinkChildren {
            get {
                yield return "OutputFile".MapFile();
                yield return "ShowProgress".MapEnum("NotSet", "LinkVerbose", "LinkVerboseLib", "LinkVerboseICF", "LinkVerboseREF", "LinkVerboseSAFESEH", "LinkVerboseCLR");
                yield return "Version".MapString();
                yield return "LinkIncremental".MapBoolean();
                yield return "SuppressStartupBanner".MapBoolean();
                yield return "IgnoreImportLibrary".MapBoolean();
                yield return "RegisterOutput".MapBoolean();
                yield return "PerUserRedirection".MapBoolean();
                yield return "AdditionalLibraryDirectories".MapFolderList();
                yield return "LinkLibraryDependencies".MapBoolean();
                yield return "UseLibraryDependencyInputs".MapBoolean();
                yield return "LinkStatus".MapBoolean();
                yield return "PreventDllBinding".MapBoolean();
                yield return "TreatLinkerWarningAsErrors".MapBoolean();
                yield return "ForceFileOutput".MapEnum("Enabled", "MultiplyDefinedSymbolOnly", "UndefinedSymbolOnly");
                yield return "CreateHotPatchableImage".MapEnum("Enabled", "X86Image", "X64Image", "ItaniumImage");
                yield return "SpecifySectionAttributes".MapString();
                yield return "MSDOSStubFileName".MapFile();
                yield return "TrackerLogDirectory".MapFolder();
                yield return "AdditionalDependencies".MapFileList();
                yield return "IgnoreAllDefaultLibraries".MapBoolean();
                yield return "IgnoreSpecificDefaultLibraries".MapFileList();
                yield return "ModuleDefinitionFile".MapFile();
                yield return "AddModuleNamesToAssembly".MapFileList();
                yield return "EmbedManagedResourceFile".MapFileList();
                yield return "ForceSymbolReferences".MapFileList();
                yield return "DelayLoadDLLs".MapFileList();
                yield return "AssemblyLinkResource".MapFileList();
                yield return "GenerateManifest".MapBoolean();
                yield return "ManifestFile".MapFile();
                yield return "AdditionalManifestDependencies".MapFileList();
                yield return "AllowIsolation".MapBoolean();
                yield return "EnableUAC".MapBoolean();
                yield return "UACExecutionLevel".MapEnum("AsInvoker", "HighestAvailable", "RequireAdministrator");
                yield return "UACUIAccess".MapBoolean();
                yield return "ManifestEmbed".MapBoolean();
                yield return "ManifestInput".MapFolderList();
                yield return "GenerateDebugInformation".MapBoolean();
                yield return "ProgramDatabaseFile".MapFile();
                yield return "StripPrivateSymbols".MapFile();
                yield return "GenerateMapFile".MapBoolean();
                yield return "MapFileName".MapFile();
                yield return "MapExports".MapBoolean();
                yield return "AssemblyDebug".MapBoolean();
                yield return "SubSystem".MapEnum("NotSet", "Console", "Windows", "Native", "EFI Application", "EFI Boot Service Driver", "EFI ROM", "EFI Runtime", "POSIX");
                yield return "MinimumRequiredVersion".MapString();
                yield return "HeapReserveSize".MapString();
                yield return "HeapCommitSize".MapString();
                yield return "StackReserveSize".MapString();
                yield return "StackCommitSize".MapString();
                yield return "LargeAddressAware".MapBoolean();
                yield return "TerminalServerAware".MapBoolean();
                yield return "SwapRunFromCD".MapBoolean();
                yield return "SwapRunFromNET".MapBoolean();
                yield return "Driver".MapEnum("NotSet", "Driver", "UpOnly", "WDM");
                yield return "OptimizeReferences".MapBoolean();
                yield return "EnableCOMDATFolding".MapBoolean();
                yield return "FunctionOrder".MapFile();
                yield return "ProfileGuidedDatabase".MapFile();
                yield return "LinkTimeCodeGeneration".MapEnum("Default", "UseLinkTimeCodeGeneration", "PGInstrument", "PGOptimization", "PGUpdate");
                yield return "MidlCommandFile".MapFile();
                yield return "IgnoreEmbeddedIDL".MapBoolean();
                yield return "MergedIDLBaseFileName".MapFile();
                yield return "TypeLibraryFile".MapFile();
                yield return "TypeLibraryResourceID".MapInt();
                yield return "AppContainer".MapBoolean();
                yield return "GenerateWindowsMetadata".MapEnum("true", "false");
                yield return "WindowsMetadataFile".MapFile();
                yield return "WindowsMetadataLinkKeyFile".MapFile();
                yield return "WindowsMetadataKeyContainer".MapFile();
                yield return "WindowsMetadataLinkDelaySign".MapBoolean();
                yield return "WindowsMetadataSignHash".MapEnum("SHA1", "SHA256", "SHA384", "SHA512");
                yield return "EntryPointSymbol".MapString();
                yield return "NoEntryPoint".MapBoolean();
                yield return "SetChecksum".MapBoolean();
                yield return "BaseAddress".MapString();
                yield return "RandomizedBaseAddress".MapBoolean();
                yield return "FixedBaseAddress".MapBoolean();
                yield return "DataExecutionPrevention".MapBoolean();
                yield return "TurnOffAssemblyGeneration".MapBoolean();
                yield return "SupportUnloadOfDelayLoadedDLL".MapBoolean();
                yield return "SupportNobindOfDelayLoadedDLL".MapBoolean();
                yield return "ImportLibrary".MapFile();
                yield return "MergeSections".MapString();
                yield return "TargetMachine".MapEnum("NotSet", "MachineARM", "MachineEBC", "MachineIA64", "MachineMIPS", "MachineMIPS16", "MachineMIPSFPU", "MachineMIPSFPU16", "MachineSH4", "MachineTHUMB", "MachineX64", "MachineX86");
                yield return "Profile".MapBoolean();
                yield return "CLRThreadAttribute".MapEnum("MTAThreadingAttribute", "STAThreadingAttribute", "DefaultThreadingAttribute");
                yield return "CLRImageType".MapEnum("ForceIJWImage", "ForcePureILImage", "ForceSafeILImage", "Default");
                yield return "LinkKeyFile".MapFile();
                yield return "KeyContainer".MapFile();
                yield return "LinkDelaySign".MapBoolean();
                yield return "SignHash".MapEnum("SHA1", "SHA256", "SHA384", "SHA512");
                yield return "CLRUnmanagedCodeCheck".MapBoolean();
                yield return "DetectOneDefinitionRule".MapBoolean();
                yield return "LinkErrorReporting".MapEnum("PromptImmediately", "QueueForNextLogin", "SendErrorReport", "NoErrorReport");
                yield return "SectionAlignment".MapInt();
                yield return "CLRSupportLastError".MapEnum("Enabled", "Disabled", "SystemDlls");
                yield return "ImageHasSafeExceptionHandlers".MapBoolean();
                yield return "AdditionalOptions".MapString();
                yield return "LinkDLL".MapBoolean();
                yield return "BuildingInIde".MapBoolean();
            }
        }

        private static IEnumerable<ToRoute> LibChildren {
            get {
                yield return "OutputFile".MapFile();
                yield return "AdditionalDependencies".MapFileList();
                yield return "AdditionalLibraryDirectories".MapFolderList();
                yield return "SuppressStartupBanner".MapBoolean();
                yield return "ModuleDefinitionFile".MapFile();
                yield return "IgnoreAllDefaultLibraries".MapBoolean();
                yield return "IgnoreSpecificDefaultLibraries".MapFileList();
                yield return "ExportNamedFunctions".MapStringList();
                yield return "ForceSymbolReferences".MapString();
                yield return "UseUnicodeResponseFiles".MapBoolean();
                yield return "LinkLibraryDependencies".MapBoolean();
                yield return "ErrorReporting".MapEnum("PromptImmediately", "QueueForNextLogin", "SendErrorReport", "NoErrorReport");
                yield return "DisplayLibrary".MapString();
                yield return "TreatLibWarningAsErrors".MapBoolean();
                yield return "TargetMachine".MapEnum("MachineARM", "MachineEBC", "MachineIA64", "MachineMIPS", "MachineMIPS16", "MachineMIPSFPU", "MachineMIPSFPU16", "MachineSH4", "MachineTHUMB", "MachineX64", "MachineX86");
                yield return "SubSystem".MapEnum("Console", "Windows", "Native", "EFI Application", "EFI Boot Service Driver", "EFI ROM", "EFI Runtime", "WindowsCE", "POSIX");
                yield return "MinimumRequiredVersion".MapString();
                yield return "RemoveObjects".MapFileList();
                yield return "Verbose".MapBoolean();
                yield return "Name".MapFile();
                yield return "LinkTimeCodeGeneration".MapBoolean();
                yield return "AdditionalOptions".MapString();
                yield return "TrackerLogDirectory".MapFolder();
            }
        }

        private static IEnumerable<ToRoute> ClCompileChildren {
            get {
                yield return "AdditionalIncludeDirectories".MapFolderList();
                yield return "AdditionalUsingDirectories".MapFolderList();
                yield return "DebugInformationFormat".MapEnum("None", "OldStyle", "ProgramDatabase", "EditAndContinue");
                yield return "CompileAsManaged".MapEnum("false", "true", "Pure", "Safe", "OldSyntax");
                yield return "CompileAsWinRT".MapBoolean();
                yield return "WinRTNoStdLib".MapBoolean();
                yield return "SuppressStartupBanner".MapBoolean();
                yield return "WarningLevel".MapEnum("TurnOffAllWarnings", "Level1", "Level2", "Level3", "Level4", "EnableAllWarnings");
                yield return "TreatWarningAsError".MapBoolean();
                yield return "SDLCheck".MapBoolean();
                yield return "TrackerLogDirectory".MapFolder();
                yield return "MultiProcessorCompilation".MapBoolean();
                yield return "ProcessorNumber".MapInt();
                yield return "Optimization".MapEnum("Disabled", "MinSpace", "MaxSpeed", "Full");
                yield return "InlineFunctionExpansion".MapEnum("Default", "Disabled", "OnlyExplicitInline", "AnySuitable");
                yield return "IntrinsicFunctions".MapBoolean();
                yield return "FavorSizeOrSpeed".MapEnum("Size", "Speed", "Neither");
                yield return "OmitFramePointers".MapBoolean();
                yield return "EnableFiberSafeOptimizations".MapBoolean();
                yield return "WholeProgramOptimization".MapBoolean();
                yield return "PreprocessorDefinitions".MapStringList();
                yield return "UndefinePreprocessorDefinitions".MapStringList();
                yield return "UndefineAllPreprocessorDefinitions".MapBoolean();
                yield return "IgnoreStandardIncludePath".MapBoolean();
                yield return "PreprocessToFile".MapBoolean();
                yield return "PreprocessOutputPath".MapString();
                yield return "PreprocessSuppressLineNumbers".MapBoolean();
                yield return "PreprocessKeepComments".MapBoolean();
                yield return "StringPooling".MapBoolean();
                yield return "MinimalRebuild".MapBoolean();
                yield return "ExceptionHandling".MapEnum("Async", "Sync", "SyncCThrow", "false");
                yield return "SmallerTypeCheck".MapBoolean();
                yield return "BasicRuntimeChecks".MapEnum("StackFrameRuntimeCheck", "UninitializedLocalUsageCheck", "EnableFastChecks", "Default");
                yield return "RuntimeLibrary".MapEnum("MultiThreaded", "MultiThreadedDebug", "MultiThreadedDLL", "MultiThreadedDebugDLL");
                yield return "StructMemberAlignment".MapEnum("1Byte", "2Bytes", "4Bytes", "8Bytes", "16Bytes", "Default");
                yield return "BufferSecurityCheck".MapBoolean();
                yield return "FunctionLevelLinking".MapBoolean();
                yield return "EnableParallelCodeGeneration".MapBoolean();
                yield return "EnableEnhancedInstructionSet".MapEnum("StreamingSIMDExtensions", "StreamingSIMDExtensions2", "AdvancedVectorExtensions", "NoExtensions", "NotSet");
                yield return "FloatingPointModel".MapEnum("Precise", "Strict", "Fast");
                yield return "FloatingPointExceptions".MapBoolean();
                yield return "CreateHotpatchableImage".MapBoolean();
                yield return "DisableLanguageExtensions".MapBoolean();
                yield return "TreatWChar_tAsBuiltInType".MapBoolean();
                yield return "ForceConformanceInForLoopScope".MapBoolean();
                yield return "RuntimeTypeInfo".MapBoolean();
                yield return "OpenMPSupport".MapBoolean();
                yield return "PrecompiledHeader".MapEnum("Create", "Use", "NotUsing");
                yield return "PrecompiledHeaderFile".MapFile();
                yield return "PrecompiledHeaderOutputFile".MapFile();
                yield return "ExpandAttributedSource".MapBoolean();
                yield return "AssemblerOutput".MapEnum("NoListing", "AssemblyCode", "AssemblyAndMachineCode", "AssemblyAndSourceCode", "All");
                yield return "UseUnicodeForAssemblerListing".MapBoolean();
                yield return "AssemblerListingLocation".MapFile();
                yield return "ObjectFileName".MapFile();
                yield return "ProgramDataBaseFileName".MapFile();
                yield return "GenerateXMLDocumentationFiles".MapBoolean();
                yield return "XMLDocumentationFileName".MapFile();
                yield return "BrowseInformation".MapBoolean();
                yield return "BrowseInformationFile".MapFile();
                yield return "CallingConvention".MapEnum("Cdecl", "FastCall", "StdCall");
                yield return "CompileAs".MapEnum("Default", "CompileAsC", "CompileAsCpp");
                yield return "DisableSpecificWarnings".MapStringList();
                yield return "ForcedIncludeFiles".MapFileList();
                yield return "ForcedUsingFiles".MapFileList();
                yield return "ShowIncludes".MapBoolean();
                yield return "EnablePREfast".MapBoolean();
                yield return "PREfastLog".MapFile();
                yield return "PREfastAdditionalOptions".MapStringList();
                yield return "PREfastAdditionalPlugins".MapStringList();
                yield return "UseFullPaths".MapBoolean();
                yield return "OmitDefaultLibName".MapBoolean();
                yield return "ErrorReporting".MapEnum("None", "Prompt", "Queue", "Send");
                yield return "TreatSpecificWarningsAsErrors".MapStringList();
                yield return "AdditionalOptions".MapString();
                yield return "BuildingInIde".MapBoolean();
            }
        }

        private static IEnumerable<ToRoute> ResourceCompileChildren {
            get {
                yield return "PreprocessorDefinitions".MapStringList();
                yield return "UndefinePreprocessorDefinitions".MapStringList();
                yield return "AdditionalIncludeDirectories".MapFolderList();
                yield return "IgnoreStandardIncludePath".MapBoolean();
                yield return "ShowProgress".MapBoolean();
                yield return "SuppressStartupBanner".MapBoolean();
                yield return "ResourceOutputFileName".MapFile();
                yield return "NullTerminateStrings".MapBoolean();
                yield return "AdditionalOptions".MapString();
                yield return "Culture".MapString();
                yield return "TrackerLogDirectory".MapFolder();
            }
        }

        private static IEnumerable<ToRoute> MidlChildren {
            get {
                yield return "PreprocessorDefinitions".MapStringList();
                yield return "AdditionalIncludeDirectories".MapFolderList();
                yield return "AdditionalMetadataDirectories".MapFolderList();
                yield return "EnableWindowsRuntime".MapBoolean();
                ;
                yield return "IgnoreStandardIncludePath".MapBoolean();
                yield return "MkTypLibCompatible".MapBoolean();
                yield return "WarningLevel".MapEnum("0", "1", "2", "3", "4");
                yield return "WarnAsError".MapBoolean();
                yield return "SuppressStartupBanner".MapBoolean();
                yield return "DefaultCharType".MapEnum("Signed", "Unsigned", "Ascii");
                yield return "TargetEnvironment".MapEnum("NotSet", "Win32", "Itanium", "ARM32", "X64");
                yield return "GenerateStublessProxies".MapBoolean();
                yield return "SuppressCompilerWarnings".MapBoolean();
                yield return "ApplicationConfigurationMode".MapBoolean();
                yield return "LocaleID".MapInt();
                yield return "OutputDirectory".MapString();
                yield return "MetadataFileName".MapFile();
                yield return "HeaderFileName".MapFile();
                yield return "DllDataFileName".MapFile();
                yield return "InterfaceIdentifierFileName".MapFile();
                yield return "ProxyFileName".MapFile();
                yield return "GenerateTypeLibrary".MapBoolean();
                yield return "TypeLibraryName".MapFile();
                yield return "GenerateClientFiles".MapEnum("Stub", "None");
                yield return "GenerateServerFiles".MapEnum("Stub", "None");
                yield return "ClientStubFile".MapFile();
                yield return "ServerStubFile".MapFile();
                yield return "TypeLibFormat".MapEnum("NewFormat", "OldFormat");
                yield return "CPreprocessOptions".MapString();
                yield return "UndefinePreprocessorDefinitions".MapStringList();
                yield return "EnableErrorChecks".MapEnum("EnableCustom", "All", "None");
                yield return "ErrorCheckAllocations".MapBoolean();
                yield return "ErrorCheckBounds".MapBoolean();
                yield return "ErrorCheckEnumRange".MapBoolean();
                yield return "ErrorCheckRefPointers".MapBoolean();
                yield return "ErrorCheckStubData".MapBoolean();
                yield return "Enumclass".MapBoolean();
                yield return "PrependWithABINamepsace".MapBoolean();
                yield return "ValidateAllParameters".MapBoolean();
                yield return "StructMemberAlignment".MapEnum("NotSet", "1", "2", "4", "8");
                yield return "RedirectOutputAndErrors".MapFile();
                yield return "AdditionalOptions".MapString();
                yield return "TrackerLogDirectory".MapFolder();
            }
        }

        public void Dispose() {
            if(ProjectCollection.GlobalProjectCollection.LoadedProjects.Contains(this)) {
                ProjectCollection.GlobalProjectCollection.UnloadProject(this);
            }
        }

        public IEnumerable<ToRoute> GetMemberRoutes(View view) {
            // an alternative method that gets access to the view while mapping.
            yield break; 
        }

        private string _rt_or_phone_check_hack_;

        internal ListWithAction<string> CheckRuntimeLibrary(string condition) {
            if (_rlCheck == null) {
                _rlCheck = new Dictionary<ProjectTargetElement, ListWithAction<string>>();
            }

            var target = LookupTarget("BeforeLink", condition, true);
            return _rlCheck.GetOrAdd(target, () => new ListWithAction<string>(s => {
                // check to see if the file is here already
                /*
                var key = (condition + "+" + s).ToLower();

                if (_copyToTargetsDuplicateCheck.Contains(key)) {
                    return;
                }
                _copyToTargetsDuplicateCheck.Add(key);
                */

                // copy it using a task
                var task = target.AddTask("CheckRuntimeLibrary");

                task.SetParameter("RuntimeLibrary", "%(ClCompile.RuntimeLibrary)");
                task.SetParameter("ExpectedRuntimeLibrary", s);
                task.SetParameter("LibraryName", SafeName);
            }));
        }

        internal ListWithAction<string> CopyToOutput(string condition) {
            if(_copyToTargets == null) {
                _copyToTargets = new Dictionary<ProjectTargetElement, ListWithAction<string>>();
            }

            var itemGroup = LookupItemGroup(condition);
            var target = LookupTarget("AfterBuild", condition, true);


            // new: add the file into an item group in the target, and only have 
            //    : one copy command in the target, using the item group.

            return _copyToTargets.GetOrAdd(target, () => new ListWithAction<string>(s => {
                // check to see if the file is here already
                var key = (condition + "+" + s).ToLower();

                if(_copyToTargetsDuplicateCheck.Contains( key ) ) {
                    return;
                }
                _copyToTargetsDuplicateCheck.Add(key);

                var cig = target.ItemGroups.FirstOrDefault() ?? target.AddItemGroup();

                // add the path to the list of items
                // if it is a folder, add the recursive ** to the end
                cig.AddItem("ItemsToCopyToOutputFolder", s.EndsWith("/") ? s + @"**" : s);
                
                // copy it using a task
                // get the first Copy task that has the same DestinationFolder as we need.
                var tsk = target.Tasks.FirstOrDefault( t  => t.Name =="Copy" && t.Parameters.ContainsKey("DestinationFolder") && t.Parameters["DestinationFolder"] == "$(TargetDir)" );
                if (tsk == null) {
                    // only create the task if there isn't one here.
                    tsk = target.AddTask("Copy");
                    tsk.SetParameter("SourceFiles", "@(ItemsToCopyToOutputFolder)");
                    tsk.SetParameter("DestinationFolder", "$(TargetDir)");
                    tsk.SetParameter("SkipUnchangedFiles", "true");
                    tsk.SetParameter("UseHardlinksIfPossible", "true");
                }
                

                // and if it's a win8 or phone8 app, make an item for it too.
                var item = itemGroup.AddItem("None", s);
                if (_rt_or_phone_check_hack_ == null) {
                    var rt = Pivots["TargetApplicationType"].Conditions["WinRT"];
                    var wp8 = Pivots["TargetApplicationType"].Conditions["Phone8"];
                    _rt_or_phone_check_hack_ = "({0}) OR ({1})".format(rt, wp8);
                    //_rt_or_phone_check_hack_  = "'$(TargetApplicationType)' == 'WinRT' Or '$(TargetApplicationType)' == 'Phone8'";
                }


                item.Condition = _rt_or_phone_check_hack_;
                item.AddMetadata("DeploymentContent", "true");
            }));
        }

        internal ListWithAction<string> EmbedInOutput(string condition) {
            if(_embedOutputs == null) {
                _embedOutputs = new Dictionary<ProjectItemGroupElement, ListWithAction<string>>();
            }

            var itemGroup = LookupItemGroup(condition);

            return _embedOutputs.GetOrAdd(itemGroup, () => new ListWithAction<string>(s => {
                var key = (condition + "+" + s).ToLower();

                if(_copyToTargetsDuplicateCheck.Contains(key)) {
                    return;
                }
                _copyToTargetsDuplicateCheck.Add(key);

                var item = itemGroup.AddItem("None", s);
                item.AddMetadata("DeploymentContent", "true");
            }));
        }

        internal ListWithAction<string> BuildTimePath(string condition) {
            // put a <setenv> task in the init target to add this directory to the 
            if(_buildTimePaths == null) {
                _buildTimePaths = new Dictionary<string, ListWithAction<string>>();
            }

            return _buildTimePaths.GetOrAdd(condition, () => new ListWithAction<string>(path => {
                if (!path.EndsWith(";")) {
                    path += ";";
                }

                var tsk = ((ProjectTargetElement)SecondInitTarget).AddTask("SetEnv");
                tsk.Condition = Pivots.GetMSBuildCondition(Name, condition);

                tsk.SetParameter("Name", "PATH");
                tsk.SetParameter("Prefix", "true");
                tsk.SetParameter("Value", path);
                tsk.AddOutputProperty("OutputEnvironmentVariable", "PATH");
            }));
        }

        public new bool Save() {
            if(Xml.Children.Count > 0) {
                if (Filename.ToLower().EndsWith(".targets")) {
                    AddConfigurations();
                }
               
                Event<Verbose>.Raise("ProjectPlus.Save", "Saving msbuild project file [{0}].", FullPath);
                FullPath.TryHardToDelete();
                base.Save();
                return true;
            }
            return false;
        }


        public IDictionary<string, string> ConditionCreate() {
            var list = Conditions;
            return new DelegateDictionary<string, string>(
                () => list,
                key => {
                    if(!list.Contains(key)) {
                        list.Add(key);
                    }
                    return key;
                },
                (s, c) => {
                    if(!list.Contains(s)) {
                        list.Add(s);
                    }
                },
                list.Remove,
                list.Clear);
        }

        internal ProjectUsingTaskElement LookupUsingTask(string name, string condition = null) {
            ProjectUsingTaskElement usingTask = null;

            var label = Pivots.GetExpressionLabel(condition);

            if(string.IsNullOrEmpty(condition)) {
                usingTask = Xml.UsingTasks.FirstOrDefault(each => name == each.TaskName && string.IsNullOrEmpty(each.Condition));
                if(usingTask != null) {
                    return usingTask;
                }
                return Xml.AddUsingTask(name, "asmfile", null);
            }

            var conditionExpression = Pivots.GetMSBuildCondition(Name, condition);
            usingTask = Xml.UsingTasks.FirstOrDefault(each => name == each.TaskName && each.Condition == conditionExpression);
            if(usingTask != null) {
                return usingTask;
            }

            usingTask = Xml.AddUsingTask(name, "asmfile", null);

            usingTask.Label = label;
            usingTask.Condition = conditionExpression;
            return usingTask;
        }

        internal ProjectImportElement LookupImport(string importPath, string condition = null) {
            ProjectImportElement import = null;

            var label = Pivots.GetExpressionLabel(condition);

            if(string.IsNullOrEmpty(condition)) {
                import = Xml.Imports.FirstOrDefault(each => importPath == each.Project && string.IsNullOrEmpty(each.Condition));
                if(import != null) {
                    return import;
                }
                return Xml.AddImport(importPath);
            }

            var conditionExpression = Pivots.GetMSBuildCondition(Name, condition);
            import = Xml.Imports.FirstOrDefault(each => importPath == each.Project && each.Condition == conditionExpression);
            if(import != null) {
                return import;
            }

            import = Xml.AddImport(importPath);

            import.Label = label;
            import.Condition = conditionExpression;
            return import;
        }

        internal ProjectImportElement LookupImport(ProjectImportGroupElement parent, string importPath, string condition = null) {
            ProjectImportElement import = null;

            var label = Pivots.GetExpressionLabel(condition);

            if(string.IsNullOrEmpty(condition)) {
                import = parent.Imports.FirstOrDefault(each => importPath == each.Project && string.IsNullOrEmpty(each.Condition));
                if(import != null) {
                    return import;
                }
                return parent.AddImport(importPath);
            }

            var conditionExpression = Pivots.GetMSBuildCondition(Name, condition);
            import = parent.Imports.FirstOrDefault(each => importPath == each.Project && each.Condition == conditionExpression);
            if(import != null) {
                return import;
            }

            import = parent.AddImport(importPath);

            import.Label = label;
            import.Condition = conditionExpression;
            return import;
        }

        internal ProjectItemDefinitionElement LookupItemDefinition(string itemType, string condition = null) {
            ProjectItemDefinitionElement itemDefinition = null;

            var label = Pivots.GetExpressionLabel(condition);

            if(string.IsNullOrEmpty(condition)) {
                itemDefinition = Xml.ItemDefinitions.FirstOrDefault(each => itemType == each.ItemType && string.IsNullOrEmpty(each.Condition));
                if(itemDefinition != null) {
                    return itemDefinition;
                }
                return Xml.AddItemDefinition(itemType);
            }

            var conditionExpression = Pivots.GetMSBuildCondition(Name, condition);
            itemDefinition = Xml.ItemDefinitions.FirstOrDefault(each => itemType == each.ItemType && each.Condition == conditionExpression);
            if(itemDefinition != null) {
                return itemDefinition;
            }

            itemDefinition = Xml.AddItemDefinition(itemType);

            itemDefinition.Label = label;
            itemDefinition.Condition = conditionExpression;
            return itemDefinition;
        }

        internal ProjectItemDefinitionElement LookupItemDefinition(ProjectItemDefinitionGroupElement parent, string itemType, string condition = null) {
            ProjectItemDefinitionElement itemDefinition = null;

            var label = Pivots.GetExpressionLabel(condition);

            if(string.IsNullOrEmpty(condition)) {
                itemDefinition = parent.ItemDefinitions.FirstOrDefault(each => itemType == each.ItemType && string.IsNullOrEmpty(each.Condition));
                if(itemDefinition != null) {
                    return itemDefinition;
                }
                return parent.AddItemDefinition(itemType);
            }

            var conditionExpression = Pivots.GetMSBuildCondition(Name, condition);
            itemDefinition = parent.ItemDefinitions.FirstOrDefault(each => itemType == each.ItemType && each.Condition == conditionExpression);
            if(itemDefinition != null) {
                return itemDefinition;
            }

            itemDefinition = parent.AddItemDefinition(itemType);

            itemDefinition.Label = label;
            itemDefinition.Condition = conditionExpression;
            return itemDefinition;
        }

        internal ProjectPropertyElement LookupProperty(string name, string condition = null) {
            ProjectPropertyElement property = null;

            var label = Pivots.GetExpressionLabel(condition);
            name = name.Replace(".", "_");

            if(string.IsNullOrEmpty(condition)) {
                property = Xml.Properties.FirstOrDefault(each => name == each.Name && string.IsNullOrEmpty(each.Condition));
                if(property != null) {
                    return property;
                }
                return Xml.AddProperty(name, "");
            }

            var conditionExpression = Pivots.GetMSBuildCondition(Name, condition);
            property = Xml.Properties.FirstOrDefault(each => name == each.Name && each.Condition == conditionExpression);
            if(property != null) {
                return property;
            }

            property = Xml.AddProperty(name, "");

            property.Label = label;
            property.Condition = conditionExpression;
            return property;
        }

        internal ProjectPropertyElement LookupProperty(ProjectPropertyGroupElement parent, string name, string condition = null) {
            ProjectPropertyElement property = null;

            var label = Pivots.GetExpressionLabel(condition);
            name = name.Replace(".", "_");

            if(string.IsNullOrEmpty(condition)) {
                property = parent.Properties.FirstOrDefault(each => name == each.Name && string.IsNullOrEmpty(each.Condition));
                if(property != null) {
                    return property;
                }
                return parent.AddProperty(name, "");
            }

            var conditionExpression = Pivots.GetMSBuildCondition(Name, condition);
            property = parent.Properties.FirstOrDefault(each => name == each.Name && each.Condition == conditionExpression);
            if(property != null) {
                return property;
            }

            property = parent.AddProperty(name, "");

            property.Label = label;
            property.Condition = conditionExpression;
            return property;
        }

        internal ProjectItemElement LookupItem(ProjectItemGroupElement parent, string name, string condition = null) {
            ProjectItemElement property = null;

            var label = Pivots.GetExpressionLabel(condition);
            name = name.Replace(".", "_");

            if(string.IsNullOrEmpty(condition)) {
                property = parent.Items.FirstOrDefault(each => name == each.ItemType && string.IsNullOrEmpty(each.Condition));
                if(property != null) {
                    return property;
                }
                return parent.AddItem(name, "UNUSED");
            }

            var conditionExpression = Pivots.GetMSBuildCondition(Name, condition);
            property = parent.Items.FirstOrDefault(each => name == each.ItemType && each.Condition == conditionExpression);
            if(property != null) {
                return property;
            }

            property = parent.AddItem(name, "UNUSED");

            property.Label = label;
            property.Condition = conditionExpression;
            
            return property;
        }

        internal ProjectPropertyGroupElement LookupPropertyGroup(string condition) {
            // look it up or create it.
            var label = Pivots.GetExpressionLabel(condition);
            ProjectPropertyGroupElement propertyGroup;

            if(string.IsNullOrEmpty(condition)) {
                propertyGroup = Xml.PropertyGroups.FirstOrDefault(each => string.IsNullOrEmpty(each.Label));
                if(propertyGroup != null) {
                    return propertyGroup;
                }
            }
            else {
                propertyGroup = Xml.PropertyGroups.FirstOrDefault(each => label == each.Label);
                if(propertyGroup != null) {
                    return propertyGroup;
                }
            }

            propertyGroup = Xml.AddPropertyGroup();
            if(!string.IsNullOrEmpty(condition)) {
                propertyGroup.Label = label;
                propertyGroup.Condition = Pivots.GetMSBuildCondition(Name, condition);
            }
            return propertyGroup;
        }

        internal ProjectItemGroupElement LookupItemGroup(string condition) {
            

            // look it up or create it.
            var label = Pivots.GetExpressionLabel(condition);
            ProjectItemGroupElement itemGroup;
            if(string.IsNullOrEmpty(condition)) {
                itemGroup = ItemGroupInitTarget.Value.ItemGroups.FirstOrDefault(each => string.IsNullOrEmpty(each.Label));
                if(itemGroup != null) {
                    return itemGroup;
                }
            }
            else {
                itemGroup = ItemGroupInitTarget.Value.ItemGroups.FirstOrDefault(each => label == each.Label);
                if(itemGroup != null) {
                    return itemGroup;
                }
            }

            itemGroup = ItemGroupInitTarget.Value.AddItemGroup();
            if(!string.IsNullOrEmpty(condition)) {
                itemGroup.Label = label;
                itemGroup.Condition = Pivots.GetMSBuildCondition(Name, condition);
            }
            return itemGroup;
        }

        internal ProjectImportGroupElement LookupImportGroup(string condition) {
            // look it up or create it.
            var label = Pivots.GetExpressionLabel(condition);
            ProjectImportGroupElement importGroup;
            if(string.IsNullOrEmpty(condition)) {
                importGroup = Xml.ImportGroups.FirstOrDefault(each => string.IsNullOrEmpty(each.Label));
                if(importGroup != null) {
                    return importGroup;
                }
            }
            else {
                importGroup = Xml.ImportGroups.FirstOrDefault(each => label == each.Label);
                if(importGroup != null) {
                    return importGroup;
                }
            }

            importGroup = Xml.AddImportGroup();
            if(!string.IsNullOrEmpty(condition)) {
                importGroup.Label = label;
                importGroup.Condition = Pivots.GetMSBuildCondition(Name, condition);
            }
            return importGroup;
        }



        internal ProjectItemDefinitionGroupElement LookupItemDefinitionGroup(string condition) {
            // look it up or create it.
            var label = Pivots.GetExpressionLabel(condition);

            if(string.IsNullOrEmpty(condition)) {
                var result = Xml.ItemDefinitionGroups.FirstOrDefault(each => string.IsNullOrEmpty(each.Label));
                if(result != null) {
                    return result;
                }
            }
            else {
                var result = Xml.ItemDefinitionGroups.FirstOrDefault(each => label == each.Label);
                if(result != null) {
                    return result;
                }
            }
            var idg = Xml.AddItemDefinitionGroup();
            if(!string.IsNullOrEmpty(condition)) {
                idg.Label = label;
                idg.Condition = Pivots.GetMSBuildCondition(Name, condition);
            }
            return idg;
        }

        internal ProjectTargetElement LookupTarget(string name, string condition = null, bool scopeToProject = false) {
            var originalName = name;

            if(string.IsNullOrEmpty(condition)) {
                switch(name) {
                    case "AfterBuild":
                    case "BeforeBuild":
                    case "BeforeLink":
                    case "Build":
                    case "Rebuild":
                    case "_PrepareForBuild":
                    case "_PrepareForRebuild":
                    case "_PrepareForClean":
                    case "LibLinkOnly":
                    case "AfterBuildCompile":
                    case "AfterBuildGenerateSources":
                        // these are predefined tasks, we'll make our own local one, and ensure we're chained to upstream one.
                        var tgt = LookupTarget(SafeName + "_" + name, null, false);
                        tgt.AfterTargets = tgt.AfterTargets.AppendToSemicolonList(name);
                        return tgt;
                }
            }
            else {
                if(scopeToProject) {
                    name = SafeName + "_" + name;
                }
            }

            var label = Pivots.GetExpressionLabel(condition);
            name = name.Replace(".", "_");

            if(string.IsNullOrEmpty(condition)) {
                var result = Xml.Targets.FirstOrDefault(each => name == each.Name && string.IsNullOrEmpty(each.Condition));
                if(result != null) {
                    return result;
                }
                return Xml.AddTarget(name);
            }

            var modifiedname = "{0}_{1}".format(name, label).Replace(" ", "_").MakeSafeFileName();

            var conditionedResult = Xml.Targets.FirstOrDefault(each => modifiedname == each.Name);
            if(conditionedResult != null) {
                return conditionedResult;
            }

            // ensure a non-conditioned gets created that we can chain to.
            LookupTarget(originalName, null, false);

            var target = Xml.AddTarget(modifiedname);

            target.Label = label;
            target.Condition = Pivots.GetMSBuildCondition(Name, condition);
            target.AfterTargets = name;

            return target;
        }

    

        public void AddConfigurations() {
            // generate init target that contains.
            

            // add property value initialization steps
            GenerateConfigurationPropertyInitializers(FirstInitTarget);
        }

        private void GenerateConfigurationPropertyInitializers(ProjectTargetElement initTarget) {
            var pivots = Pivots;
            ProjectPropertyGroupElement pg = null;

            foreach(var pivot in pivots.Values) {
                // dynamic cfg = configurationsView.GetProperty(pivot);
                IEnumerable<string> choices = pivot.Choices.Keys;

                if(!pivot.IsBuiltIn) {
                    // add init steps for this.
                    var finalPropName = "{0}-{1}".format(pivot.Name, SafeName);

                    foreach(var choice in choices.Distinct()) {
                        var choicePropName = "{0}-{1}".format(pivot.Name, choice);
                        
                        var tsk = initTarget.AddTask("StringContains");
                        tsk.SetParameter("Text", choicePropName);
                        tsk.SetParameter("Library", SafeName);
                        tsk.SetParameter("Value", choice);
                        tsk.Condition = @"'$({0})'==''".format(finalPropName);
                        tsk.AddOutputProperty("Result", finalPropName);
                    }
                    pg = pg ?? Xml.AddPropertyGroup();
                    pg.Label = "Default initializers for properties";
                    pg.AddProperty(finalPropName, choices.FirstOrDefault()).Condition = @"'$({0})' == ''".format(finalPropName);
                }
            }
        }

        internal ProjectPropertyGroupElement AddPropertyInitializer(string propertyName, string conditionExpression, string value, ProjectPropertyGroupElement ppge = null) {
            ppge = ppge ?? Xml.AddPropertyGroup();
            ppge.Label = "Additional property initializers";
            ppge.AddProperty(propertyName, value).Condition = conditionExpression;
            return ppge;
        }
    }
}