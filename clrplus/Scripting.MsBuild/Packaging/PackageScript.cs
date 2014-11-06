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
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Management.Automation.Runspaces;
    using System.Reflection;
    using System.Text.RegularExpressions;
    using System.Xml.Linq;
    using Building.Tasks;
    using Core.Collections;
    using Core.DynamicXml;
    using Core.Exceptions;
    using Core.Extensions;
    using Core.Tasks;
    using Languages.PropertySheet;
    using Languages.PropertySheetV3;
    using Languages.PropertySheetV3.Mapping;
    using Microsoft.Build.Evaluation;
    using Microsoft.Build.Tasks;
    using Platform;
    using Powershell.Core;
    using Warning = Core.Tasks.Warning;

    public class PackageScript : IDisposable {
        private static readonly string _requiredTemplate = File.ReadAllText(Path.Combine(NugetPackage.etcPath, "PackageScriptTemplate.autopkg"));
        private static readonly string _requiredTemplateStatic = File.ReadAllText(Path.Combine(NugetPackage.etcPath, "PackageScriptTemplate_static.autopkg"));
        private static readonly string _requiredTemplateLTCG = File.ReadAllText(Path.Combine(NugetPackage.etcPath, "PackageScriptTemplate_ltcg.autopkg"));
        
        internal readonly Pivots Pivots;
        internal string PackageDirectory {  
            get {
                return Directory.GetParent(FullPath).FullName;
            }
        }
        internal readonly string FullPath;
        private bool _initialized;
        
        internal NugetPackage NugetPackage; 
        
        private bool _processed;
        private RootPropertySheet _sheet;

        private dynamic _nuspec;
        private dynamic _nuget;
        private dynamic _nugetfiles;
        private readonly List<string> _dependentNuGetPackageDirectories = new List<string>();
        private IDictionary<string, string> _macros = new Dictionary<string, string>();

        private List<string> _tempFiles = new List<string>();

        public long SplitThreshold {get; set;}

        public PackageScript(string filename) {
            SplitThreshold = 1024*1024*20; // 20 megabytes default

            Event<Verbose>.Raise("PackageScript", "Constructor");
            _sheet = new RootPropertySheet(this);

            // get the full path to the .autopkgFile
            FullPath = filename.GetFullPath();

            // parse the script
            _sheet.ParseFile(filename);
            
            
            _sheet.ImportText(_requiredTemplate, "required");

            // temp hack to work around static & ltcg getting marked as used when they are just in the template.
            var scriptText = File.ReadAllText(filename);
            if (scriptText.IndexOf("static", StringComparison.InvariantCultureIgnoreCase) > -1) {
                _sheet.ImportText(_requiredTemplateStatic, "required_static");    
            }

            // end hack
            if (scriptText.IndexOf("ltcg", StringComparison.InvariantCultureIgnoreCase) > -1) {
                _sheet.ImportText(_requiredTemplateLTCG, "required_ltcg");
            }
            // ensure we have at least the package ID
            var packageName = _sheet.View.nuget.nuspec.id;
            if (string.IsNullOrEmpty(packageName)) {
                throw new ClrPlusException("the Field nuget.nuspec.id can not be null or empty. You must specify an id for a package.");
            }

            // set the package name macro 
            _sheet.AddMacro("pkgname", packageName);
            _sheet.CurrentView.AddMacroHandler( (name, context) => _macros.ContainsKey(name.ToLower()) ? _macros[name.ToLower()] : null);
            _sheet.CurrentView.AddMacroHandler((name, context) => System.Environment.GetEnvironmentVariable(name));
            
            Pivots = new Pivots(_sheet.CurrentView.GetProperty("configurations"));
        }

        public void AddMacro(string key, string value) {
            _macros.AddOrSet(key.ToLower(), value);
        }

        public void AddNuGetPackageDirectory(string directory) {
            _dependentNuGetPackageDirectories.Add(directory);
        }
        public void Dispose() {
            if (NugetPackage != null) {
                NugetPackage.Dispose();
                NugetPackage = null;
            }
            _sheet = null;
        }

        private string NormalizeOuptutKey(string key) {
            if (string.IsNullOrEmpty(key)) {
                return "default";
            }
            return key.Replace('/', ',').Replace('\\', ',').Replace('&', ',').SplitToList(',', ' ').OrderBy(each => each).Aggregate((current, each) => current + ',' + each).Trim(',');
        }

        public void SaveSource() {
            _sheet.SaveFile(FullPath);
        }

        /// <exception cref="ClrPlusException">Fatal Error.</exception>
        private void Fail(bool isFatal) {
            if (isFatal) {
                throw new ClrPlusException("Fatal Error.");
            }
        }

        /// <exception cref="ClrPlusException">Fatal Error.</exception>
        private void FailAlways(bool whocares) {
            throw new ClrPlusException("Fatal Error.");
        }

        private void InitializeNuget() {
            View nugetView = _sheet.View.nuget;
            _nuget = nugetView;

            if (!nugetView.HasChildren ) {
                FailAlways(Event<SourceError>.Raise("AP100", _sheet.CurrentView.SourceLocations, "script does not contain a declaration for a NuGet package"));
            }

            _nuspec = _nuget.nuspec;

            if(!_nuspec.HasChildren) {
                FailAlways(Event<SourceError>.Raise("AP102", nugetView.SourceLocations, "script does not contain a 'nuspec' declaration in 'nuget'"));
            }


            if(string.IsNullOrEmpty(_nuspec.id.Value)) {
                FailAlways(Event<SourceError>.Raise("AP103", _nuspec.SourceLocations, "script does not contain a 'id' declaration in 'nuspec'"));
            }

            NugetPackage = new NugetPackage(this, PackageRole.@default, _nuspec.id.Value);
            nugetView.AddChildRoutes(NugetPackage.Initialize());

            // do the property sheet mapping
            var conditions = new XDictionary<string, string>();

            // map the file routes 
            nugetView.AddChildRoute("files".MapTo(new object() , new [] {
                "condition".MapTo(conditions, key => Pivots.GetExpressionFilepath(_nuspec.id, nugetView.ResolveMacrosInContext(key))),
                "*".MapTo(conditions, key => Pivots.GetExpressionFilepath(_nuspec.id, nugetView.ResolveMacrosInContext(key)))
            }));

            var nuspecid = _nuspec.id;

            var conditionFolderMacroHander = (GetMacroValueDelegate)((macro, context) => {
                if (macro == "conditionFolder") {
                    return LinqExtensions.SingleItemAsEnumerable(Pivots.GetExpressionFilepath(nuspecid, ((View)context).GetSingleMacroValue("ElementId")??""));
                }
                return null;
            });
            _nuget.props.AddMacroHandler(conditionFolderMacroHander);
            _nuget.targets.AddMacroHandler(conditionFolderMacroHander);

            nugetView.AddChildRoute("props".MapTo(NugetPackage.Props.Value /*, GetPropsProject("default").ProjectRoutes() */));
            
            // always need a targets
            nugetView.AddChildRoute("targets".MapTo(NugetPackage.Targets.Value/*, GetTargetsProject("default").ProjectRoutes() */));

            // Keep track of any user custom tasks that should be defined.
            // TODO: the CustomTasks.Add is wrong below. It gets called three times, rather than just once for a single input.
            //       But I do not understand how to link it to the CUustomTask list above there.
            // TODO: Make sure this runs before targets (how!?) so that it gets a chance to initalize and send info to
            //       the legal task list in MsBuildMap.cs.
            nugetView.AddChildRoute(
                "customtasks".MapTo(NugetPackage.CustomTasks, 
                new[] { 
                    "task".MapTo(() => { var nv = new CustomTaskInfo(); NugetPackage.CustomTasks.Add(nv); return nv; })
                }));

            // other variants/frameworks
        }

        public void Initialize(PackageTypes packageTypes = PackageTypes.All) {
            if (_initialized) {
                return;
            }

            Event<Verbose>.Raise("PackageScript.Initialize", "Init package script");

            if (packageTypes.HasFlag(PackageTypes.NuGet) ) {
               InitializeNuget();
            }

            _initialized = true;
        }

        private void ProcessNuget() {
            _nugetfiles = _nuget.files;
            if(!_nugetfiles.HasChildren) {
                Fail(Event<SourceWarning>.Raise("AP200", _nuget.SourceLocations, "script does not contain a 'files' declaration in 'nuget'"));
            }
            Event<Verbose>.Raise("PackageScript.ProcessNuget", "Processing Nuget Files");
            // process files
            ProcessNugetFiles(_nugetfiles, PackageDirectory, null);
            Event<Verbose>.Raise("PackageScript.ProcessNuget", "Done Processing Nuget Files");

            NugetPackage.Process();


        }

        private void ProcessCoApp() {
            
        }

        public void Process(PackageTypes packageTypes = PackageTypes.All) {
            if (!_initialized) {
                Initialize(packageTypes);
            }

            if (_processed) {
                return;
            }

            Event<Verbose>.Raise("PackageScript.Process", "Processing Package Creation");

            // persist the propertysheet to the msbuild model.
            _sheet.View.CopyToModel();

            Event<Verbose>.Raise("PackageScript.Process", "(copy to model, done)");

            if (packageTypes.HasFlag(PackageTypes.NuGet)) {
                ProcessNuget();
            }

            if(packageTypes.HasFlag(PackageTypes.CoApp)) {
                ProcessCoApp();
            }
            _processed = true;
        }

        public string Save(PackageTypes packageTypes, bool cleanIntermediateFiles, bool generateOnly,  out IEnumerable<string> overlayPackages ) {
            if (generateOnly) {
                cleanIntermediateFiles = false;
            }
            
            if (!_processed) {
                Process();
            }

            NugetPackage.SplitThreshold = SplitThreshold;
            NugetPackage.NoSplit = NoSplit;
            
            var result =  NugetPackage.Save(cleanIntermediateFiles, generateOnly,  out overlayPackages);

            // clean up our temporary files when we're done.
            foreach (var f in _tempFiles) {
                f.TryHardToDelete();
            }

            _tempFiles.Clear();

            // and clean out the renamed files folder when we're done too.
            Path.Combine(FilesystemExtensions.TempPath,"renamedFiles").TryHardToDelete();

            return result;

        }

        

        private void ProcessNugetFiles(View filesView, string srcFilesRoot, string currentCondition) {
            currentCondition = Pivots.NormalizeExpression(currentCondition ?? "");

            foreach (var containerName in filesView.GetChildPropertyNames().ToArray()) {
                View container = filesView.GetProperty(containerName);
                if (containerName == "condition" || containerName == "*") {
                    foreach (var condition in container.GetChildPropertyNames()) {
                        ProcessNugetFiles(container.GetElement(condition), srcFilesRoot, condition);
                    }
                    continue;
                }
                
                // GS01 Workaround for bug in Values not caching the output set when a collection is added to ?
                var filemasks = container.Values.Distinct().ToArray();
                var relativePaths = new Dictionary<string, string>();

                var optionExcludes = container.GetMetadataValues("exclude", container, false).Union(container.GetMetadataValues("excludes", container, false)).ToArray();

                foreach (var mask in filemasks) {
                    if (string.IsNullOrEmpty(mask)) {
                        continue;
                    }
                    var fileset = mask.FindFilesSmarterComplex(srcFilesRoot, optionExcludes).GetMinimalPathsToDictionary();

                    if (!fileset.Any()) {
                        Event<Warning>.Raise("ProcessNugetFiles","WARNING: file selection '{0}' failed to find any files ", mask);
                        continue;
                    }
                    foreach (var key in fileset.Keys) {
                        relativePaths.Add(key, fileset[key]);
                    }
                }
                
                var optionRenames = container.GetMetadataValues("rename", container, false).Union(container.GetMetadataValues("renames", container, false)).Select(each => {
                    var s = each.Split('/','\\');
                    if (s.Length == 2) {
                        return new {
                            search = new Regex(s[0]),
                            replace = s[1]
                        };
                    }
                    return null;
                }).Where( each => each != null).ToArray();
             
                // determine the destination location in the target package
                var optionDestination = container.GetMetadataValueHarder("destination", currentCondition);
                var destinationFolder = string.IsNullOrEmpty(optionDestination) ? (filesView.GetSingleMacroValue("d_" + containerName) ?? "\\") : optionDestination;

                var optionFlatten = container.GetMetadataValueHarder("flatten", currentCondition).IsPositive();
                var optionNoOverlay  = container.GetMetadataValueHarder("overlay", currentCondition).IsNegative();
              
                var addEachFiles = container.GetMetadataValuesHarder("add-each-file", currentCondition).ToArray();
                var addFolders = container.GetMetadataValuesHarder("add-folder", currentCondition).ToArray();
                var addAllFiles = container.GetMetadataValuesHarder("add-all-files", currentCondition).ToArray();


                // add the folder to an Collection somewhere else in the PropertySheet
                if (addFolders.Length > 0 && relativePaths.Any() ) {
                    foreach (var addFolder in addFolders) {
                        var folderView = filesView.GetProperty(addFolder, lastMinuteReplacementFunction: s => s.Replace("${condition}", currentCondition));
                        if (folderView != null) {
                            folderView.AddValue((filesView.GetSingleMacroValue("pkg_root") + destinationFolder).FixSlashes());
                        }
                    }
                }

                // add the folder+/** to an Collection somewhere else in the PropertySheet (useful for making <ItemGroup>s)
                if (addAllFiles.Length > 0 && relativePaths.Any()) {
                    foreach (var addAllFile in addAllFiles) {
                        var folderView = filesView.GetProperty(addAllFile, lastMinuteReplacementFunction: s => s.Replace("${condition}", currentCondition));

                        if (folderView != null) {
                            // add the /** suffix on the path
                            // so it can be used in an ItemGroup
                            folderView.AddValue(((filesView.GetSingleMacroValue("pkg_root") + destinationFolder) + "/**").FixSlashes());
                        }
                    }
                }

                foreach (var srcf in relativePaths.Keys) {
                    var src = srcf;

                    if (optionExcludes.HasWildcardMatch(src)) {
                        continue;
                    }

                    Event<Verbose>.Raise("ProcessNugetFiles (adding file)", "'{0}' + '{1}'", destinationFolder, relativePaths[src]);
                    string target = Path.Combine(destinationFolder, optionFlatten ? Path.GetFileName(relativePaths[src]) : relativePaths[src]).Replace("${condition}", currentCondition).FixSlashes();

                    if (optionRenames.Length > 0) {
                        // process rename commands
                        var dir = Path.GetDirectoryName(target);
                        var filename = Path.GetFileName(target);
                        
                        foreach (var rename in optionRenames) {
                            var newFilename = rename.search.Replace(filename, rename.replace);
                            
                            if (newFilename != filename) {

                                // generate the new location for the renamed file
                                var tmpFile = Path.Combine(FilesystemExtensions.TempPath,"renamedFiles",Guid.NewGuid().ToString(), newFilename);

                                // derp. gotta create the target dir first. *sigh*
                                Directory.CreateDirectory(Path.GetDirectoryName(tmpFile));

                                //copy the src file to the tmpFile location
                                File.Copy(src, tmpFile);
                                
                                // remove it when we're done packaging
                                _tempFiles.Add(tmpFile);

                                // switch the src to use the temp file
                                src = tmpFile;

                                // and just use the dir as the target (instead of the whole path)
                                target = dir;
                                break;
                            }
                        }
                        
                    }

                    // add the file under the configuration (unless it's marked for no overlay, in which case just put it in the base.)
                    NugetPackage.AddFile(src, target, optionNoOverlay ? string.Empty : currentCondition);

                    if (addEachFiles.Length > 0) {
                        foreach (var addEachFile in addEachFiles) {
                            var fileListView = filesView.GetProperty(addEachFile, lastMinuteReplacementFunction: s=> s.Replace("${condition}", currentCondition));
                            if (fileListView != null) {
                                fileListView.AddValue((filesView.GetSingleMacroValue("pkg_root") + target).Replace("${condition}", currentCondition).FixSlashes());
                            }
                        }
                    }
                }
            }
        }

        public IEnumerable<string> AllPackages {
            get {
                return NugetPackage.Packages;
            }
        }

        public bool NoSplit {get; set;}

        private IEnumerable<ToRoute> MapDependencies() {
            yield break;
        }

        public bool Validate() {
            return true;
        }
    }
}

