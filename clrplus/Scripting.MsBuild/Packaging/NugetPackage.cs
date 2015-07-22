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
    using System.IO;
    using System.Linq;
    using System.Management.Automation.Runspaces;
    using System.Reflection;
    using System.Runtime.ExceptionServices;
    using System.Text;
    using System.Xml.Linq;
    using Core.DynamicXml;
    using Core.Exceptions;
    using Core.Extensions;
    using Core.Tasks;
    using Languages.PropertySheetV3;
    using Languages.PropertySheetV3.Mapping;
    using Microsoft.Build.Construction;
    using Microsoft.Build.Evaluation;
    using Platform;
    using Powershell.Core;
    using Utility;

    internal enum PackageRole {
        @default,
        overlay,
    }

    internal class NugetPackage : IProjectOwner {
        internal static string NuGetNativeExtensionVersion = "NuGet-NativeExtensionVersion";
        internal static string NuGetNativeExtensionVersionValue = ((UInt64)((FourPartVersion)Assembly.GetExecutingAssembly().Version())).ToString();
        internal static string NuGetNativeExtensionVersionCondition = "'$({0})' == '' OR $({0}) < {1}".format(NuGetNativeExtensionVersion, NuGetNativeExtensionVersionValue);

        internal static string NuGetNativeExtensionPath = "NuGet-NativeExtensionPath";
        internal static string NuGetNativeExtensionPathValue = @"$(MSBuildThisFileDirectory)\private";
        internal static string NuGetNativeExtensionPathCondition = @"'$({0})' == '' OR '$({1})' =='' OR $({1}) < {2}".format(NuGetNativeExtensionPath, NuGetNativeExtensionVersion, NuGetNativeExtensionVersionValue);

        internal static string[] MSBuildExtensionTasks = { "NuGetPackageOverlay", "CheckRuntimeLibrary", "StringContains" };

        internal static string NuGetPackageOverlayTaskAssembly = @"$({0})\coapp.NuGetNativeMSBuildTasks.dll".format(NuGetNativeExtensionPath);

        internal static string etcPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "etc");

        private static readonly string _defaultUIProperties = Assembly.GetExecutingAssembly().ExtractFileResource("default-ui-properties.xml");

        private static int MinimumPivotSize = 10*1024; // 1k for testing. 10k for real life.

        // private readonly Dictionary<string, string> _files = new Dictionary<string, string>();
        private readonly Dictionary<string, Dictionary<string,string>> _fileSets = new Dictionary<string, Dictionary<string, string>>();
        private readonly PackageScript _packageScript;
        private readonly string _pkgName;  // whole name of the package (ie 'zlib' or 'zlib.overlay')
        internal readonly PackageRole PkgRole;  // role of the package (ie 'default' or 'overlay')

        internal long SplitThreshold;

        // private readonly Dictionary<string, ProjectPlus> _props = new Dictionary<string, ProjectPlus>();
        // private readonly Dictionary<string, ProjectPlus> _targets = new Dictionary<string, ProjectPlus>();

        internal Lazy<ProjectPlus> Props;
        internal Lazy<ProjectPlus> Targets;

        public string ProjectName {
            get {
                return _pkgName;
            }
        }

        internal string SafeName {
            get {
                return ProjectName.MakeSafeFileName().Replace(".", "_");
            }
        }

        internal string NuspecFilename {
            get {
                return _pkgName + ".nuspec";
            }
        }

        public string FullPath {
            get {
                return Path.Combine(Directory, NuspecFilename);
            }
        }

        public string Directory {
            get {
                return _packageScript.PackageDirectory;
            }
        }

        private dynamic _nuSpec = new DynamicNode("package");

        internal NugetPackage(PackageScript packageScript, PackageRole packageRole, string packageName)
        {
            _packageScript = packageScript;
            _pkgName = packageName;
            PkgRole = packageRole;
           
            Props = new Lazy<ProjectPlus>(() => new ProjectPlus(this, "{0}.props".format(_pkgName)));
            Targets = new Lazy<ProjectPlus>(() => new ProjectPlus(this, "{0}.targets".format(_pkgName)));

            _nuSpec.metadata.id = "Package";
            _nuSpec.metadata.version = "1.0.0";
            // _nuSpec.metadata.authors = "NAME";
            // _nuSpec.metadata.owners = "NAME";
            // _nuSpec.metadata.licenseUrl = "http://LICENSE_URL_HERE_OR_DELETE_THIS_LINE";
            // _nuSpec.metadata.projectUrl = "http://PROJECT_URL_HERE_OR_DELETE_THIS_LINE";
            // _nuSpec.metadata.iconUrl = "http://ICON_URL_HERE_OR_DELETE_THIS_LINE";
            _nuSpec.metadata.requireLicenseAcceptance = "false";
            // _nuSpec.metadata.description = "Package description";
            // _nuSpec.metadata.releaseNotes = "Summary of changes made in this release of the package.";
            // _nuSpec.metadata.copyright = "Copyright 2013";
        }

        public Pivots Pivots {
            get {
                return _packageScript.Pivots;
            }
        }

        public void Dispose() {
            // properly dispose of the projects (ie, unload them)
            if (Props.IsValueCreated) {
                Props.Value.Dispose();
            }
            if (Targets.IsValueCreated) {
                Targets.Value.Dispose();
            }
        }

        public void AddFile(string sourcePath, string destinationPath, string currentCondition) {
            destinationPath = destinationPath.FixSlashes();
            var fileCollection = _fileSets.GetOrAdd( currentCondition.Is() ? currentCondition : string.Empty, () => new Dictionary<string, string>());

            if (fileCollection.ContainsKey(destinationPath)) {
                Event<Error>.Raise("AP100", "Duplicate file '{0}' added to NuGet package from source '{1}'", destinationPath, sourcePath);
            }

            fileCollection.Add(destinationPath, sourcePath);
        }

        public bool IsDefault {
            get {
                return PkgRole == PackageRole.@default;
            }
        }

        internal IEnumerable<ToRoute> Initialize() {
            var results = Enumerable.Empty<ToRoute>();

            switch (PkgRole) {
                case PackageRole.@default:
                    // only the default package gets to map to the propertysheet directly.
                    results = results.Concat(MapNugetNode());
                    break;
                case PackageRole.overlay:
                    break;
                default:
                    break;
            }
            return results;
        }

        private IEnumerable<ToRoute> MapNugetNode() {
            // only the default package gets to do this_nuSpec.metadata.version.
            yield return "nuspec".MapTo(new object(), new [] {
             "id".MapTo(() => (string)_nuSpec.metadata.id, v => _nuSpec.metadata.id = v.SafeToString()),
             "version".MapTo(() => (string)_nuSpec.metadata.version, v => _nuSpec.metadata.version = v.SafeToString()),
             "title".MapTo(() => (string)_nuSpec.metadata.title, v => _nuSpec.metadata.title = v.SafeToString()),
             "authors".MapTo(() => (string)_nuSpec.metadata.authors, v => _nuSpec.metadata.authors = v.SafeToString()),
             "owners".MapTo(() => (string)_nuSpec.metadata.owners, v => _nuSpec.metadata.owners = v.SafeToString()),
             "description".MapTo(() => (string)_nuSpec.metadata.description, v => _nuSpec.metadata.description = v.SafeToString()),
             "summary".MapTo(() => (string)_nuSpec.metadata.summary, v => _nuSpec.metadata.summary = v.SafeToString()),
             "releaseNotes".MapTo(() => (string)_nuSpec.metadata.releaseNotes, v => _nuSpec.metadata.releaseNotes = v.SafeToString()),
             "copyright".MapTo(() => (string)_nuSpec.metadata.copyright, v => _nuSpec.metadata.copyright = v.SafeToString()),
             "language".MapTo(() => (string)_nuSpec.metadata.language, v => _nuSpec.metadata.language = v.SafeToString()),
             "tags".MapTo(() => (string)_nuSpec.metadata.tags, v => _nuSpec.metadata.tags = v.SafeToString().Replace(","," ")),

             "licenseUrl".MapTo(() => (string)_nuSpec.metadata.licenseUrl, v => _nuSpec.metadata.licenseUrl = v.SafeToString()),
             "projectUrl".MapTo(() => (string)_nuSpec.metadata.projectUrl, v => _nuSpec.metadata.projectUrl = v.SafeToString()),
             "iconUrl".MapTo(() => (string)_nuSpec.metadata.iconUrl, v => _nuSpec.metadata.iconUrl = v.SafeToString()),

             "requireLicenseAcceptance".MapTo(() => ((string)_nuSpec.metadata.requireLicenseAcceptance).IsPositive() ? "true" : "false", v => _nuSpec.metadata.requireLicenseAcceptance = v.SafeToString().IsTrue().ToString().ToLower())});
            
                
                    // map the dependencies node into generating 
            yield return "dependencies.packages".MapTo(new ListWithOnChanged<string>(list => {
                // when the list changes, set the value of the correct xml elements
                _nuSpec.metadata.dependencies = null;
                _nuSpec.metadata.Add("dependencies");
                foreach (var i in list) {
                    var node = _nuSpec.metadata.dependencies.Add("dependency");
                    var item = i.ToString();

                    var p = item.IndexOf('/');

                    if (p > -1) {
                        node.Attributes.id = item.Substring(0, p);
                        node.Attributes.version = item.Substring(p + 1);
                    } else {
                        node.Attributes.id = item;
                    }
                }
            }));

        }

        internal void Process() {
            Event<Verbose>.Raise("NugetPackage.Process", "Processing nuget package [{0}].", NuspecFilename);
            
            switch(PkgRole) {
                case PackageRole.@default:
                    break;

                case PackageRole.overlay:
                    //copy the nuspec fields from the default project (and change what's needed)
                    var basePackage = _packageScript.NugetPackage;
                    _nuSpec = new DynamicNode(new XElement(basePackage._nuSpec.Element));

                    _nuSpec.metadata.requireLicenseAcceptance = "false";
                    _nuSpec.metadata.title = "{0} Overlay".format((string)basePackage._nuSpec.metadata.title);
                    _nuSpec.metadata.summary = "Additional components for package '{0}'".format(basePackage._pkgName);
                    _nuSpec.metadata.id = _pkgName;
                    _nuSpec.metadata.description = "Additional components for package '{0}'. This package should only be installed as a dependency. \r\n(This is not the package you are looking for).".format(basePackage._pkgName);
                    _nuSpec.metadata.dependencies = null;
                    _nuSpec.metadata.tags = null;

                    _nuSpec.metadata.authors = (string)basePackage._nuSpec.metadata.authors;
                    _nuSpec.metadata.owners = (string)basePackage._nuSpec.metadata.owners;
                    _nuSpec.metadata.licenseUrl = (string)basePackage._nuSpec.metadata.licenseUrl;
                    _nuSpec.metadata.projectUrl = (string)basePackage._nuSpec.metadata.projectUrl;
                    _nuSpec.metadata.iconUrl = (string)basePackage._nuSpec.metadata.iconUrl;

                    _nuSpec.metadata.releaseNotes = (string)basePackage._nuSpec.metadata.releaseNotes;
                    _nuSpec.metadata.copyright = (string)basePackage._nuSpec.metadata.copyright;

                    break;

                default:
                    basePackage = _packageScript.NugetPackage;
                    _nuSpec = new DynamicNode(new XElement(basePackage._nuSpec.Element));

                    _nuSpec.metadata.requireLicenseAcceptance = "false";
                    _nuSpec.metadata.id = _pkgName;
                    _nuSpec.metadata.description = "*unknown*";
                    _nuSpec.metadata.dependencies = null;
                    break;
            }
        }

        internal void Validate() {
            // Event<Verbose>.Raise("NugetPackage.Validate", "Validating nuget package (nothing)");
        }

        internal string Save(bool cleanIntermediateFiles, bool generateOnly  ,out IEnumerable<string> overlayPackages ) {
            
            List<string> overlays = null;
            string packageFileName = null;
            // overlays never really happen for overlay packages.
            IEnumerable<string> tmpOverlayPackages;

            // clear out the nuspec files node.
            _nuSpec.files = null;
            var temporaryFiles = new List<string>();

            var files = _nuSpec.Add("files");

            if (PkgRole == PackageRole.@default) {

                // default xamlUi
                var xamlText = _defaultUIProperties;
                if (xamlText.Is()) {
                    var targetFilename = @"default-propertiesui.xml";
                    var xamlPath = Path.Combine(Directory, targetFilename);
                    xamlPath.TryHardToDelete();
                    File.WriteAllText(xamlPath, xamlText);
                    temporaryFiles.Add(xamlPath);
                    AddFileToNuSpec(xamlPath, @"\build\native\{0}".format(targetFilename));
                    Targets.Value.Xml.AddItemGroup().AddItem("PropertyPageSchema", @"$(MSBuildThisFileDirectory)\{0}".format(targetFilename));
                }

                // generated xaml
                var xaml = GenerateSettingsXaml();
                if (xaml != null) {
                    var targetFilename = @"{0}-propertiesui-{1}.xml".format(_pkgName, Guid.NewGuid());
                    var xamlPath = Path.Combine(Directory, targetFilename);
                    xamlPath.TryHardToDelete();
                    Event<Verbose>.Raise("NugetPackage.Save", "Saving xaml file [{0}].", xamlPath);
                    xaml.Save(xamlPath);
                    temporaryFiles.Add(xamlPath);
                    AddFileToNuSpec(xamlPath, @"\build\native\{0}".format(targetFilename));
                    Targets.Value.Xml.AddItemGroup().AddItem("PropertyPageSchema", @"$(MSBuildThisFileDirectory)\{0}".format(targetFilename));
                }


                // save the /build/configurations.autopkg file 
                var configurationsFilename = @"configurations.autopkg";
                var cfgPath = Path.Combine(Directory, configurationsFilename);
                cfgPath.TryHardToDelete();
                SaveConfigurationFile(cfgPath);
                temporaryFiles.Add(cfgPath);
                AddFileToNuSpec(cfgPath, @"\build\{0}".format(configurationsFilename));

                var publisherInfoFilename = @"publisher-info.txt";
                var pifPath = Path.Combine(Directory, publisherInfoFilename);
                pifPath.TryHardToDelete();
                SavePifFile(pifPath);
                temporaryFiles.Add(pifPath);
                AddFileToNuSpec(pifPath, @"\build\{0}".format(publisherInfoFilename));

                string tags = _nuSpec.metadata.tags;
                tags = tags.Replace(",", " ");

                if (tags.IndexOf("nativepackage") == -1) {
                    tags = tags + " nativepackage";
                }

                _nuSpec.metadata.tags = tags;


                // always add the msbuild extensions to the main package
                AddFileToNuSpec(Path.Combine(etcPath, "CoApp.NuGetNativeMSBuildTasks.dll.orig"), @"\build\native\private\CoApp.NuGetNativeMSBuildTasks.dll.orig");

                // first, register all the tasks
                foreach (var t in MSBuildExtensionTasks) {
                    var usingTask = Targets.Value.Xml.AddUsingTask(t, NuGetPackageOverlayTaskAssembly, null);
                    usingTask.Condition = "'$(DesignTimeBuild)' != 'true' AND ('$(NugetMsBuildExtensionLoaded)' == '' OR '$(NugetMsBuildExtensionLoaded)' == 'false')";
                }

                
                // 'declare' the  property in props
                var pg = Props.Value.Xml.AddPropertyGroup();
                var prop = pg.AddProperty("NugetMsBuildExtensionLoaded", "false");
                prop.Condition = "'$(NugetMsBuildExtensionLoaded)' == '' OR '$(NuGet-OverlayLoaded)' == 'false'";

                prop = pg.AddProperty(NuGetNativeExtensionPath, NuGetNativeExtensionPathValue);
                prop.Condition = NuGetNativeExtensionPathCondition;


                // 'declare' the properties in global scope/
                var propName = "Needs-{0}".format(_pkgName).Replace(".", "_");
                pg.AddProperty(propName, "");
                pg.Condition = "'$({0})' == '' OR '$({0})' == '*Undefined*'".format(propName);

                propName = "Needs-{0}-Version".format(_pkgName).Replace(".", "_");
                pg.AddProperty(propName, "");
                pg.Condition = "'$({0})' == '' OR '$({0})' == '*Undefined*'".format(propName);


                var initTarget = Targets.Value.EarlyInitTarget.Value;
                var copyTask = initTarget.AddTask("Copy");
                copyTask.SetParameter("SkipUnchangedFiles", "true");
                copyTask.SetParameter("SourceFiles", @"$(NuGet-NativeExtensionPath)\coapp.NuGetNativeMSBuildTasks.dll.orig");
                copyTask.SetParameter("DestinationFiles", @"$(NuGet-NativeExtensionPath)\coapp.NuGetNativeMSBuildTasks.dll");

                pg = initTarget.AddPropertyGroup();
                prop = pg.AddProperty("NugetMsBuildExtensionLoaded", "true");
                prop.Condition = "'$(NugetMsBuildExtensionLoaded)' == '' OR '$(NuGet-OverlayLoaded)' == 'false'";

                // then add the NuGetPackageOverlay tasks into the init target 
                // var task = Targets.Value.LookupTarget("BeforeBuild").AddTask("CheckRuntimeLibrary");

                // task.SetParameter("RuntimeLibrary", "%(ClCompile.RuntimeLibrary)");
                // task.SetParameter("ExpectedRuntimeLibrary", @"$(ExpectedRuntimeLibrary)");
                // task.SetParameter("LibraryName", SafeName);
                // task.SetParameter("Configuration", "");
            }
           
            Event<Verbose>.Raise("NugetPackage.Save", "Saving nuget spec file to [{0}].", FullPath);

            // this is where we decide if we're going to split this into overlays or not.
            if (!NoSplit && (PkgRole == PackageRole.@default && _fileSets.Keys.Count > 1 && _fileSets.Keys.SelectMany(set => _fileSets[set].Values).Sum(srcPath => new FileInfo(srcPath).Length) > SplitThreshold)) {
                // ok, this package is gonna get split 

                // first, add the init target stuff in the .props
                var initTarget = Props.Value.EarlyInitTarget.Value;
                var pg = initTarget.AddPropertyGroup();

                // version check.
                var wantVer = ((UInt64)((FourPartVersion)(string)_nuSpec.metadata.version)).ToString();

                var nvPropName = "Needs-{0}-Version".format(_pkgName).Replace(".", "_");
                var propName = "Needs-{0}".format(_pkgName).Replace(".", "_");
                
                var prop = pg.AddProperty(nvPropName, (string)_nuSpec.metadata.version);
                prop.Condition = "'$({0})' == '' OR $({0}) < {1} ".format(propName, wantVer);
               
                prop = pg.AddProperty(propName, wantVer);
                prop.Condition = "'$({0})' == '' OR $({0}) < {1} ".format(propName, wantVer);

                // then add the init target stuff in the .targets

                // now, iterate thru each file set, and create an overlay package for just those files
                // (If the size of a whole file set is less than 100k, we'll just add it to the main package)
                foreach (var set in _fileSets.Keys.Where(each => each.Is())) {
                    long setSize = _fileSets[set].Values.Sum(srcPath => new FileInfo(srcPath).Length);
                    if (setSize < (MinimumPivotSize)) {
                        foreach (var src in _fileSets[set].Keys) {
                            AddFileToNuSpec(_fileSets[set][src], src);
                        }
                    } else {
                        overlays = overlays ?? new List<string>();

                        var overlayPackageName = "{0}.overlay-{1}".format(_pkgName, Pivots.GetExpressionFilename("", set));

                        // create a seperate package file
                        using (var pkg = new NugetPackage(_packageScript, PackageRole.overlay, overlayPackageName)) {
                            pkg._fileSets.Add(string.Empty, _fileSets[set]);
                            pkg.Process();

                            pkg.Save(cleanIntermediateFiles, generateOnly ,out tmpOverlayPackages);

                            // add each overlay package created to the master list of overlays
                            overlays.Add(overlayPackageName);

                            // iterate thru all the files in the base set, and add them to this package's list
                            foreach (var src in _fileSets[string.Empty].Keys) {
                                AddFileToNuSpec(_fileSets[string.Empty][src], src);
                            }

                            // then add the NuGetPackageOverlay tasks into the init target 
                            var task = Targets.Value.EarlyInitTarget.Value.AddTask("NuGetPackageOverlay");

                            task.SetParameter("Package", overlayPackageName);
                            task.SetParameter("Version", "$({0})".format(nvPropName));
                            task.SetParameter("PackageDirectory", @"$(MSBuildThisFileDirectory)\..\..");
                            task.SetParameter("SolutionDirectory", @"$(SolutionDir)");

                            // set the condition for the overlay to the appropriate condition pivot.
                            task.Condition = Pivots.GetMSBuildCondition(Targets.Value.Name, set);
                        }

                    }
                }

                if (overlays != null && overlays.Count > 0) {
                    // add the two DLLs and the nuget.exe into the package.
                  
                    AddFileToNuSpec(Path.Combine(etcPath, "CoApp.NuGetNativeExtensions.dll"), @"\build\native\private\CoApp.NuGetNativeExtensions.dll");
                    AddFileToNuSpec(Path.Combine(etcPath, "nuget.exe"), @"\build\native\private\nuget.exe");

                    // add the cmd script to the root of the package
                    var cmdScriptPath = Path.Combine(etcPath, "nuget-overlay.cmd");

                    var scriptText = File.ReadAllText(cmdScriptPath).Replace("$$VERSION$$", (string)_nuSpec.metadata.version);

                    var scriptPath = Path.Combine(Directory, @"NuGet-Overlay.cmd");
                    scriptPath.TryHardToDelete();
                    File.WriteAllText(scriptPath, scriptText);
                    temporaryFiles.Add(scriptPath);
                    AddFileToNuSpec(scriptPath, @"\NuGet-Overlay.cmd");

                    var pivotListPath = Path.Combine(Directory, @"pivot-list.txt");
                    pivotListPath.TryHardToDelete();
                    File.WriteAllLines(pivotListPath, overlays.ToArray());
                    temporaryFiles.Add(pivotListPath);
                    AddFileToNuSpec(pivotListPath, @"\build\native\pivot-list.txt");

                }
            } else {
                // single package 
                foreach (var set in _fileSets.Keys) {
                    foreach (var src in _fileSets[set].Keys) {
                        AddFileToNuSpec(_fileSets[set][src], src);
                    }
                }
            } 

            if (Props.IsValueCreated && Props.Value.Xml.Children.Count > 0 ) {
                Props.Value.FullPath.TryHardToDelete();
                if (Props.Value.Save()) {
                        temporaryFiles.Add(Props.Value.FullPath);
                        AddFileToNuSpec(Props.Value.FullPath, @"\build\native\{0}".format(Props.Value.Filename));
                    }
            }

            if (Targets.IsValueCreated && Targets.Value.Xml.Children.Count > 0) {
                Targets.Value.FullPath.TryHardToDelete();
                if (Targets.Value.Save()) {
                    temporaryFiles.Add(Targets.Value.FullPath);
                    AddFileToNuSpec(Targets.Value.FullPath, @"\build\native\{0}".format(Targets.Value.Filename));
                }
            }
         
            _nuSpec.Save(FullPath);
            temporaryFiles.Add(FullPath);

            if (PkgRole == PackageRole.@default || !_fileSets.Values.IsNullOrEmpty())
            { 
                // don't save the package if it has no files in it.
                if (!generateOnly) {
                    packageFileName = NuPack(FullPath);
                    Event<OutputObject>.Raise(new FileInfo(packageFileName.GetFullPath()));
                }
            }

            if (generateOnly) {
                foreach (var t in temporaryFiles) {
                    Event<OutputObject>.Raise( new FileInfo(t.GetFullPath()));
                }
            }

            if (cleanIntermediateFiles) {
                temporaryFiles.ForEach( FilesystemExtensions.TryHardToDelete );
            }
            overlayPackages = overlays ?? Enumerable.Empty<string>();

            return packageFileName;
        }

        private List<string> _packages;
        public IEnumerable<string> Packages { get {
            return _packages.IsNullOrEmpty() ? Enumerable.Empty<string>() : _packages;
        }}

        public bool NoSplit {get; set;}

        private bool SaveConfigurationFile(string cfgPath) {
            var sb = new StringBuilder();
            sb.Append("configurations {\r\n");

            foreach (var pivot in Pivots.Values) {
                if (pivot.UsedChoices.Any()) {
                    var used = pivot.Choices.Keys.First().SingleItemAsEnumerable().Union(pivot.UsedChoices).ToArray();
                    // yep, do this one.
                    sb.Append("    ").Append(pivot.Name).Append(" { \r\n"); // Platform {
                    if (pivot.IsBuiltIn) {
                        sb.Append("        key : \"").Append(pivot.Key).Append("\";\r\n"); //    key : "Platform"; 
                    }
                    sb.Append("        choices : { ").Append(used.Aggregate((current, each) => current + ", " + each)).Append(" };\r\n"); // choices: { Win32, x64, ARM, AnyCPU };
                    foreach(var ch in used) {
                        if (pivot.Choices[ch].Count() > 1) {
                            sb.Append("        ").Append(ch).Append(".aliases : { ").Append(pivot.Choices[ch].Aggregate((current, each) => current + ", " + each)).Append(" };\r\n"); //Win32.aliases : { x86, win32, ia32, 386 };
                        }
                    }
                    sb.Append("    };\r\n");
                }
            }
            sb.Append("};\r\n");

            File.WriteAllText(cfgPath, sb.ToString());
            return true;
        }

        private bool SavePifFile(string pifPath) {
            var sb = new StringBuilder();
            sb.Append("Package Created: {0}\r\n".format(DateTime.Now.ToUniversalTime()));
            sb.Append("CoApp tools version: {0}\r\n".format(this.Assembly().Version()));

            File.WriteAllText(pifPath, sb.ToString());
            return true;
        }

        private void AddFileToNuSpec(string src, string dest) {
            var file = _nuSpec.files.Add("file");
            var fullSrcPath = src.GetFullPath().Replace("\\\\", "\\");
            
            var relativeSrcPath = Directory.RelativePathTo(fullSrcPath);
            
            file.Attributes.src = relativeSrcPath;

            file.Attributes.target = dest.FixSlashes();
        }

        /*
        internal ProjectPlus GetTargetsProject(string frameworkVariant) {
            return GetOrCreateProject("targets",frameworkVariant);
        }

        internal ProjectPlus GetPropsProject(string frameworkVariant) {
            return GetOrCreateProject("props", frameworkVariant);
        }
        

        internal ProjectPlus GetOrCreateProject(string projectFileExtension, string frameworkVariant) {
            frameworkVariant = frameworkVariant.WhenNullOrEmpty("native");

            switch (projectFileExtension) {
                case "targets":
                    if (!_targets.ContainsKey(frameworkVariant)) {
                        Event<Verbose>.Raise("NugetPackage.GetOrCreateProject", "Creating .targets for [{0}] in role [{1}].", frameworkVariant, PkgRole.ToString());
                        _targets.Add(frameworkVariant, new ProjectPlus(this, "{0}.targets".format(_pkgName)));
                    }
                    return _targets[frameworkVariant];


                case "props":
                    if (!_props.ContainsKey(frameworkVariant)) {
                        Event<Verbose>.Raise("NugetPackage.GetOrCreateProject", "Creating .props for [{0}] in role [{1}].", frameworkVariant, PkgRole.ToString());
                        _props.Add(frameworkVariant, new ProjectPlus(this, "{0}.props".format(_pkgName)));
                    }
                    return _props[frameworkVariant];
            }

            throw new ClrPlusException("Unknown project extension '{0}' ".format(projectFileExtension));
        }
        */


        public string NuPack(string path) {
            string packageFileName = null;
            using(dynamic ps = Runspace.DefaultRunspace.Dynamic()) {
                DynamicPowershellResult results = ps.InvokeExpression(@"nuget.exe pack ""{0}"" 2>&1".format(path));

                bool lastIsBlank = false;
                foreach(var r in results) {
                    string s = r.ToString();
                    if (string.IsNullOrWhiteSpace(s)) {
                        if (lastIsBlank) {
                            continue;
                        }
                        lastIsBlank = true;
                    } else {
                        if (s.IndexOf("Issue: Assembly outside lib folder") > -1) {
                            continue;
                        }
                        if(s.IndexOf("folder and hence it won't be added as reference when the package is installed into a project") > -1) {
                            continue;
                        }
                        if (s.IndexOf("Solution: Move it into the 'lib' folder if it should be referenced") > -1) {
                            continue;
                        }
                        if(s.IndexOf("issue(s) found with package") > -1) {
                            continue;
                        }

                        if (s.IndexOf("Successfully created package '") > -1) {
                            

                            var scp = s.IndexOf('\'') + 1;
                            var pkg = s.Substring(scp, s.LastIndexOf('\'') - scp);
                            packageFileName = pkg;
                            if (pkg.Length > 0) {
                                (_packages ?? (_packages = new List<string>())).Add(pkg);
                            }
                        }
                        lastIsBlank = false;
                    }
                    
                    // Issue: Assembly outside lib folder.
                    // Description: The assembly 'build\native\bin\Win32\v110\Release\WinRT\casablanca110.winrt.dll' is not inside the 'lib' folder and hence it won't be added as reference when the package is installed into a project.
                    // Solution: Move it into the 'lib' folder if it should be referenced.
                    
                    // Event<Message>.Raise(" >", "{0}", s);
                    if (s.Is()) {
                        Event<Progress>.Raise("Runing 'NuGet Pack'", -1, "{0}", s);
                    }
                }
                if (results.LastIsTerminatingError) {
                    throw new ClrPlusException("NuGet Pack Failed");
                }
            }
            return packageFileName;
        }

        private dynamic InitXaml() {
            dynamic node = new DynamicNode("ProjectSchemaDefinitions", "clr-namespace:Microsoft.Build.Framework.XamlTypes;assembly=Microsoft.Build.Framework");
            var rule = node.Add("Rule");

            rule.Attributes.Name = "ReferencedPackages{0}".format(Guid.NewGuid());
            rule.Attributes.PageTemplate = "tool";
            rule.Attributes.DisplayName = "Referenced Packages";
            rule.Attributes.SwitchPrefix = "/";
            rule.Attributes.Order = "1";

            var categories = rule.Add("Rule.Categories");
            var category = categories.Add("Category");
            category.Attributes.Name = _pkgName;
            category.Attributes.DisplayName = _pkgName;

            var datasources = rule.Add("Rule.DataSource");
            var datasource = datasources.Add("DataSource");
            datasource.Attributes.Persistence = "ProjectFile";
            datasource.Attributes.ItemType = "";

            return node;
        }

        

        private dynamic GenerateSettingsXaml() {
            if (!IsDefault) {
                return null;
            }
            dynamic xaml = null;

            foreach (var pivot in Pivots.Values.Where(pivot => !pivot.IsBuiltIn && pivot.UsedChoices.Any() )) {

                xaml = xaml ?? InitXaml();

                var defaultchoice = pivot.Choices.Keys.FirstOrDefault();

                // add the key
                var enumProperty = xaml.Rule.Add("EnumProperty");
                enumProperty.Attributes.Name = "{0}-{1}".format(pivot.Name, _pkgName);
                enumProperty.Attributes.DisplayName = pivot.Name;
                enumProperty.Attributes.Description = pivot.Description;
                enumProperty.Attributes.Category = _pkgName;

                // add the choices
                var used = pivot.Choices.Keys.First().SingleItemAsEnumerable().Union(pivot.UsedChoices).ToArray();

                foreach (var v in used) {
                    var enumValue = enumProperty.Add("EnumValue");
                    enumValue.Attributes.Name = (v == defaultchoice) ? "" : v; // store "" as the value for defaultchoice.
                    enumValue.Attributes.DisplayName = pivot.Descriptions[v];
                }
            }

            return xaml;
        }
    }
}