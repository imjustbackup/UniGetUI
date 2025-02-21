using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using UniGetUI.Core.Classes;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Managers.PowerShellManager
{
    public abstract class BaseNuGet : PackageManager
    {
        public sealed override void Initialize()
        {
            if (DetailsHelper is not BaseNuGetDetailsHelper)
            {
                throw new InvalidOperationException("NuGet-based package managers must not reassign the PackageDetailsProvider property");
            }

            if (!Capabilities.SupportsCustomVersions)
            {
                throw new InvalidOperationException("NuGet-based package managers must support custom versions");
            }

            if (!Capabilities.SupportsCustomPackageIcons)
            {
                throw new InvalidOperationException("NuGet-based package managers must support custom versions");
            }

            base.Initialize();
        }

        private struct SearchResult
        {
            public string version;
            public CoreTools.Version version_float;
            public string id;
        }

        protected sealed override IReadOnlyList<Package> FindPackages_UnSafe(string query)
        {
            List<Package> Packages = [];
            INativeTaskLogger logger = TaskLogger.CreateNew(Enums.LoggableTaskType.FindPackages);

            IReadOnlyList<IManagerSource> sources;
            if (Capabilities.SupportsCustomSources)
            {
                sources = SourcesHelper.GetSources();
            }
            else
            {
                sources = [ Properties.DefaultSource ];
            }

            foreach(IManagerSource source in sources)
            {
                Uri SearchUrl = new($"{source.Url}/Search()?searchTerm=%27{HttpUtility.UrlEncode(query)}%27&targetFramework=%27%27&includePrerelease=false");
                logger.Log($"Begin package search with url={SearchUrl} on manager {Name}");

                using HttpClient client = new(CoreData.GenericHttpClientParameters);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
                HttpResponseMessage response = client.GetAsync(SearchUrl).GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    logger.Error($"Failed to fetch api at Url={SearchUrl} with status code {response.StatusCode}");
                    continue;
                }

                string SearchResults = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                MatchCollection matches = Regex.Matches(SearchResults, "<entry>([\\s\\S]*?)<\\/entry>");

                Dictionary<string, SearchResult> AlreadyProcessedPackages = [];

                foreach (Match match in matches)
                {
                    if (!match.Success)
                    {
                        continue;
                    }

                    string id = Regex.Match(match.Value, "Id='([^<>']+)'").Groups[1].Value;
                    string version = Regex.Match(match.Value, "Version='([^<>']+)'").Groups[1].Value;
                    var float_version = CoreTools.VersionStringToStruct(version);
                    // Match title = Regex.Match(match.Value, "<title[ \\\"\\=A-Za-z0-9]+>([^<>]+)<\\/title>");

                    if (AlreadyProcessedPackages.TryGetValue(id, out var value) && value.version_float >= float_version)
                    {
                        continue;
                    }

                    AlreadyProcessedPackages[id] = new SearchResult { id = id, version = version, version_float = float_version };
                }
                foreach (SearchResult package in AlreadyProcessedPackages.Values)
                {
                    logger.Log($"Found package {package.id} version {package.version} on source {source.Name}");
                    Packages.Add(new Package(CoreTools.FormatAsName(package.id), package.id, package.version, source, this));
                }
            }

            logger.Close(0);

            return Packages;
        }

        protected sealed override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
        {
            var logger = TaskLogger.CreateNew(LoggableTaskType.ListUpdates);

            var installedPackages = TaskRecycler<IReadOnlyList<IPackage>>.RunOrAttach(GetInstalledPackages);
            var Packages = new List<Package>();

            Dictionary<IManagerSource, List<IPackage>> sourceMapping = new();

            foreach (var package in installedPackages)
            {
                var uri = package.Source;
                if (!sourceMapping.ContainsKey(uri)) sourceMapping[uri] = new();
                sourceMapping[uri].Add(package);
            }

            foreach (var pair in sourceMapping)
            {
                var packageIds = new StringBuilder();
                var packageVers = new StringBuilder();
                foreach (var package in pair.Value)
                {
                    packageIds.Append(package.Id + "|");
                    packageVers.Append(package.VersionString + "|");
                }

                var SearchUrl = $"{pair.Key.Url.ToString().Trim('/')}/GetUpdates()" +
                                $"?packageIds=%27{HttpUtility.UrlEncode(packageIds.ToString())}%27" +
                                $"&versions=%27{HttpUtility.UrlEncode(packageVers.ToString())}%27" +
                                $"&includePrerelease=0&includeAllVersions=0";

                using HttpClient client = new(CoreData.GenericHttpClientParameters);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
                HttpResponseMessage response = client.GetAsync(SearchUrl).GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    logger.Error($"Failed to fetch api at Url={SearchUrl} with status code {response.StatusCode}");
                }
                else
                {
                    string SearchResults = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    MatchCollection matches = Regex.Matches(SearchResults, "<entry>([\\s\\S]*?)<\\/entry>");

                    Dictionary<string, SearchResult> AlreadyProcessedPackages = [];

                    foreach (Match match in matches)
                    {
                        if (!match.Success)
                        {
                            continue;
                        }

                        string id = Regex.Match(match.Value, "<d:Id>'([^</']+)</'").Groups[1].Value;
                        string version = Regex.Match(match.Value, "<d:Version>'([^</']+)</'").Groups[1].Value;
                        var float_version = CoreTools.VersionStringToStruct(version);
                        // Match title = Regex.Match(match.Value, "<title[ \\\"\\=A-Za-z0-9]+>([^<>]+)<\\/title>");

                        if (AlreadyProcessedPackages.TryGetValue(id, out var value) && value.version_float >= float_version)
                        {
                            continue;
                        }

                        AlreadyProcessedPackages[id] = new SearchResult { id = id, version = version, version_float = float_version };
                    }
                    foreach (SearchResult package in AlreadyProcessedPackages.Values)
                    {
                        logger.Log($"Found package {package.id} version {package.version} on source {pair.Key.Name}");
                        Packages.Add(new Package(CoreTools.FormatAsName(package.id), package.id, package.version, package.version, pair.Key, this));
                    }
                }
            }

            return Packages;
        }
    }

}
