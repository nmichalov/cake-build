#tool dotnet:?package=GitVersion.Tool&version=5.8.1
#tool dotnet:?package=dotnet-xunit-to-junit&version=3.0.2

#r Newtonsoft.Json

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");

var assemblyVersion = "1.0.0";
var packageVersion = "1.0.0";

var artifactsDir = MakeAbsolute(Directory("artifacts"));
var testsResultsDir = artifactsDir.Combine(Directory("tests-results"));
var packagesDir = artifactsDir.Combine(Directory("packages"));

var solutionPath = "./build.sln";

Task("Clean")
    .Does(() =>
    {
        CleanDirectory(artifactsDir);

        var settings = new DotNetCleanSettings
        {
            Configuration = configuration
        };

        DotNetClean(solutionPath, settings);
    });

Task("Restore")
    .IsDependentOn("Clean")
    .Does(() =>
    {
        DotNetRestore();
    });

Task("SemVer")
    .IsDependentOn("Restore")
    .Does(() =>
    {
        var gitVersionSettings = new GitVersionSettings
        {
            NoFetch = true,
        };

        var gitVersion = GitVersion(gitVersionSettings);

        assemblyVersion = gitVersion.AssemblySemVer;
        packageVersion = gitVersion.NuGetVersion;

        Information($"AssemblySemVer: {assemblyVersion}");
        Information($"NuGetVersion: {packageVersion}");
    });

Task("SetAppVeyorVersion")
    .IsDependentOn("Semver")
    .WithCriteria(() => AppVeyor.IsRunningOnAppVeyor)
    .Does(() =>
    {
        AppVeyor.UpdateBuildVersion(packageVersion);
    });

Task("Build")
    .IsDependentOn("SetAppVeyorVersion")
    .Does(() =>
    {
        var settings = new DotNetBuildSettings
        {
            Configuration = configuration,
            NoIncremental = true,
            NoRestore = true,
            MSBuildSettings = new DotNetMSBuildSettings()
                .SetVersion(assemblyVersion)
                .WithProperty("FileVersion", packageVersion)
                .WithProperty("InformationalVersion", packageVersion)
                .WithProperty("nowarn", "7035")
        };

        if (IsRunningOnLinuxOrDarwin())
        {
            settings.Framework = "netstandard2.0";

            GetFiles("./src/*/*.csproj")
                .ToList()
                .ForEach(f => DotNetBuild(f.FullPath, settings));

            settings.Framework = "net6.0";

            GetFiles("./tests/*/*Tests.csproj")
                .ToList()
                .ForEach(f => DotNetBuild(f.FullPath, settings));
        }
        else
        {
            DotNetBuild(solutionPath, settings);
        }
    });

Task("Test")
    .IsDependentOn("Build")
    .Does(() =>
    {
        var testResultsFile = testsResultsDir.Combine("{assembly}.{framework}.xml");

        var settings = new DotNetTestSettings
        {
            Configuration = configuration,
            NoBuild = true,
            Loggers = new List<string>() { $"\"xunit;LogFilePath={testResultsFile}\"" }
        };

        if (IsRunningOnLinuxOrDarwin())
        {
            settings.Framework = "net6.0";
        }

        DotNetTest(solutionPath, settings);
    })
    .Does(() =>
    {
        if (IsRunningOnCircleCI())
        {
            TransformCircleCITestResults();
        }
    })
    .DeferOnError();

Task("Pack")
    .IsDependentOn("Test")
    .WithCriteria(() => HasArgument("pack"))
    .Does(() =>
    {
        var settings = new DotNetPackSettings
        {
            Configuration = configuration,
            NoBuild = true,
            NoRestore = true,
            IncludeSymbols = true,
            OutputDirectory = packagesDir,
            MSBuildSettings = new DotNetMSBuildSettings()
                .WithProperty("PackageVersion", packageVersion)
                .WithProperty("Copyright", $"Copyright Contoso {DateTime.Now.Year}")
        };

        if (IsRunningOnLinuxOrDarwin())
        {
            settings.MSBuildSettings.WithProperty("TargetFrameworks", "netstandard2.0");
        }

        GetFiles("./src/*/*.csproj")
            .ToList()
            .ForEach(f => DotNetPack(f.FullPath, settings));
    });

Task("PublishAppVeyorArtifacts")
    .IsDependentOn("Pack")
    .WithCriteria(() => HasArgument("pack") && AppVeyor.IsRunningOnAppVeyor)
    .Does(() =>
    {
        CopyFiles($"{packagesDir}/*.nupkg", MakeAbsolute(Directory("./")), false);

        GetFiles($"./*.nupkg")
            .ToList()
            .ForEach(f => AppVeyor.UploadArtifact(f, new AppVeyorUploadArtifactsSettings { DeploymentName = "packages" }));
    });

Task("Default")
    .IsDependentOn("PublishAppVeyorArtifacts");

RunTarget(target);

/// <summary>
/// - No .NET 4.6.x Framework installed, only .NET 6.x
/// </summary>
private bool IsRunningOnLinuxOrDarwin()
{
    return Context.Environment.Platform.IsUnix();
}

private bool IsRunningOnCircleCI()
{
    return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CIRCLECI"));
}

private void TransformCircleCITestResults()
{
    // CircleCI infer the name of the testing framework from the containing folder
    var testResultsCircleCIDir = artifactsDir.Combine("junit");
    EnsureDirectoryExists(testResultsCircleCIDir);

    var testResultsFiles = GetFiles($"{testsResultsDir}/*.xml");

    foreach (var testResultsFile in testResultsFiles)
    {
        var inputFilePath = testResultsFile;
        var outputFilePath = testResultsCircleCIDir.CombineWithFilePath(testResultsFile.GetFilename());

        var arguments = new ProcessArgumentBuilder()
            .AppendQuoted(inputFilePath.ToString())
            .AppendQuoted(outputFilePath.ToString())
            .Render();

        var toolName = Context.Environment.Platform.IsUnix() ? "dotnet-xunit-to-junit" : "dotnet-xunit-to-junit.exe";

        var settings = new DotNetToolSettings
        {
            ToolPath = Context.Tools.Resolve(toolName)
        };

        DotNetTool(arguments, settings);
    }
}
