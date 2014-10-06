<?xml version="1.0" encoding="utf-8"?>
<Project InitialTargets="Info" ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Import Condition="$(HAS_COMMON_VARIABLES) != 'true' AND Exists('$(registry:HKEY_LOCAL_MACHINE\Software\Outercurve\CoApp.Powershell\etc)\common-variables.vcxproj')" Project="$(registry:HKEY_LOCAL_MACHINE\Software\Outercurve\CoApp.Powershell\etc)\common-variables.vcxproj" />
  
  <PropertyGroup>
    <ImportPositionOK>true</ImportPositionOK>
    <ImportPositionOK Condition="$(ConfigurationType.ToLower().IndexOf('library'))!=-1 AND ('$(PlatformPropsFound)'=='true' OR '$(ToolsetPropsFound)'=='true' OR '$(OutDirWasSpecified)'=='true')">false</ImportPositionOK>
  </PropertyGroup>
  <PropertyGroup Condition="$(ConfigurationType.ToLower().IndexOf('library'))!=-1">
    <ConfigurationType Condition="'$(UsesConfigurationType.ToLower())'!='ltcg'">$(UsesConfigurationType)Library</ConfigurationType>
    <ConfigurationType Condition="'$(UsesConfigurationType.ToLower())'=='ltcg'">StaticLibrary</ConfigurationType>
  </PropertyGroup>
  <PropertyGroup Condition="'$(OverrideConfigurationType)' != '' ">
    <ConfigurationType>$(OverrideConfigurationType)</ConfigurationType>
  </PropertyGroup>
  <PropertyGroup>
    <LinkIncremental>false</LinkIncremental>
    <LibName Condition="'|$(LibName)|'=='||'">$(ProjectName)</LibName>
    <UseDebugLibraries Condition="$(Configuration.ToLower().IndexOf('debug'))!=-1">true</UseDebugLibraries>
    <UseDebugLibraries Condition="$(Configuration.ToLower().IndexOf('debug'))==-1">false</UseDebugLibraries>
    <OutName>$(LibName)$(OutNameSuffix)</OutName>
    <ProjectRootDir Condition="'$(SolutionDir)' != ''">$(SolutionDir)</ProjectRootDir>
    <ProjectRootDir Condition="'$(SolutionDir)' == ''">./</ProjectRootDir>
    <TargetName>$(OutName)</TargetName>
    <OutDir>$(ProjectRootDir)Output/$(PlatformToolset)/$(Platform)/$(Configuration)/$(UsesConfigurationType)/$(CallingConvention)/</OutDir>
    <IntDir>$(ProjectRootDir)Intermediate/$(TargetName)/$(PlatformToolset)/$(Platform)/$(Configuration)/$(UsesConfigurationType)/$(CallingConvention)/</IntDir>
  </PropertyGroup>
  <ItemDefinitionGroup>
    <ClCompile>
      <CallingConvention>$(CallingConvention)</CallingConvention>
      <RuntimeLibrary Condition="$(Configuration.ToLower().IndexOf('debug'))!=-1 AND ('$(UsesConfigurationType.ToLower())'=='static' OR '$(UsesConfigurationType.ToLower())'=='ltcg')">MultiThreadedDebug</RuntimeLibrary>
      <RuntimeLibrary Condition="$(Configuration.ToLower().IndexOf('debug'))==-1 AND ('$(UsesConfigurationType.ToLower())'=='static' OR '$(UsesConfigurationType.ToLower())'=='ltcg')">MultiThreaded</RuntimeLibrary>
      <WarningLevel>Level3</WarningLevel>
    </ClCompile>
    <Lib>
      <LinkTimeCodeGeneration Condition="'$(UsesConfigurationType.ToLower())'=='ltcg'">true</LinkTimeCodeGeneration>
    </Lib>
    <Link>
      <GenerateDebugInformation>true</GenerateDebugInformation>
      <OutputFile Condition="'$(ConfigurationType.ToLower())'=='dynamiclibrary'">$(OutDir)$(OutName).dll</OutputFile>
      <OutputFile Condition="'$(ConfigurationType.ToLower())'=='staticlibrary'">$(OutDir)$(OutName).lib</OutputFile>
      <ProgramDatabaseFile>$(OutDir)$(OutName).pdb</ProgramDatabaseFile>
      <ImportLibrary Condition="'$(ConfigurationType.ToLower())'=='dynamiclibrary'">$(OutDir)$(OutName).lib</ImportLibrary>
      <ProfileGuidedDatabase>$(OutDir)$(OutName).pgd</ProfileGuidedDatabase>
      <AdditionalLibraryDirectories>$(OutDir);%(AdditionalLibraryDirectories)</AdditionalLibraryDirectories>
    </Link>
  </ItemDefinitionGroup>
  <ItemDefinitionGroup Condition="$(Configuration.ToLower().IndexOf('debug'))!=-1">
    <ClCompile>
      <Optimization>Disabled</Optimization>
    </ClCompile>
  </ItemDefinitionGroup>
  <ItemDefinitionGroup Condition="$(Configuration.ToLower().IndexOf('debug'))==-1">
    <ClCompile>
      <Optimization>MaxSpeed</Optimization>
    </ClCompile>
  </ItemDefinitionGroup>
  <Target Name="Info" BeforeTargets="Build">
    <Error Condition="'$(ImportPositionOK)'=='false'" File="$(MSBuildProjectFullPath)"
           Text="Import of %22$(MSBuildThisFile)%22 must be before import of %22%24(VCTargetsPath)\Microsoft.Cpp.Default.props%22 in Dynamic and Static library projects." />
    <Message Text="*******************************************************************************" Importance="high" />
    <Message Text="*  Configuration          :  '$(Configuration)'" Importance="high" />
    <Message Text="*  PlatformToolset        :  '$(PlatformToolset)'" Importance="high" />
    <Message Text="*  Platform               :  '$(Platform)'" Importance="high" />
    <Message Text="*  ConfigurationType      :  '$(ConfigurationType)'" Importance="high" />
    <Message Text="*  UsesConfigurationType  :  '$(UsesConfigurationType)'" Importance="high" />
    <Message Text="*  CallingConvention      :  '$(CallingConvention)'" Importance="high" />
    <Message Text="*******************************************************************************" Importance="high" />
    <Message Importance="high" Text="Building in vs?: '$(BuildingInsideVisualStudio)'/'$(VisualStudioVersion)'" />
    <MakeDir Directories="$(OutDir);$(IntDir);$(TargetDir)"/>
  </Target>
</Project>
