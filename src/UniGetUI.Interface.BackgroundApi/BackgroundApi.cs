using System.Text;
using System.Web;
using Nancy;
using Nancy.Hosting.Self;
using UniGetUI.Core.Data;
using UniGetUI.Core.IconEngine;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Interface
{
    internal static class ApiTokenHolder
    {
        public static string Token = "";
    }

    public class BackgroundApiRunner
    {
        public event EventHandler<EventArgs>? OnOpenWindow;
        public event EventHandler<EventArgs>? OnOpenUpdatesPage;
        public event EventHandler<KeyValuePair<string, string>>? OnShowSharedPackage;
        public event EventHandler<EventArgs>? OnUpgradeAll;
        public event EventHandler<string>? OnUpgradeAllForManager;
        public event EventHandler<string>? OnUpgradePackage;

        private bool __running;

        public BackgroundApiRunner()
        {
            BackgroundApi.OnOpenWindow += (s, e) => OnOpenWindow?.Invoke(s, e);
            BackgroundApi.OnOpenUpdatesPage += (s, e) => OnOpenUpdatesPage?.Invoke(s, e);
            BackgroundApi.OnShowSharedPackage += (s, e) => OnShowSharedPackage?.Invoke(s, e);
            BackgroundApi.OnUpgradeAll += (s, e) => OnUpgradeAll?.Invoke(s, e);
            BackgroundApi.OnUpgradeAllForManager += (s, e) => OnUpgradeAllForManager?.Invoke(s, e);
            BackgroundApi.OnUpgradePackage += (s, e) => OnUpgradePackage?.Invoke(s, e);
        }

        public static bool AuthenticateToken(string token)
        {
            return token == ApiTokenHolder.Token;
        }

        /// <summary>
        /// Run the background api and wait for it for being stopped with the Stop() method
        /// </summary>
        public async Task Start()
        {
            try
            {

                if (Settings.Get("DisableWidgetsApi"))
                {
                    Logger.Warn("Widgets API is disabled");
                    return;
                }

                ApiTokenHolder.Token = CoreTools.RandomString(64);

                __running = true;
                NancyHost host;
                try
                {
                    host = new NancyHost(new HostConfiguration { RewriteLocalhost = false, }, new Uri("http://localhost:7058/"));
                    host.Start();
                }
                catch
                {
                    host = new NancyHost(new Uri("http://localhost:7058/"));
                    host.Start();
                }

                Settings.SetValue("CurrentSessionToken", ApiTokenHolder.Token);
                Logger.Info("Randomly-generated background API auth token for the current session: " + ApiTokenHolder.Token);

                Logger.Info("Api running on http://localhost:7058");

                while (__running)
                {
                    await Task.Delay(100);
                }
                host.Stop();
                Logger.Info("Api was shut down");
            }
            catch (Exception e)
            {
                Logger.Error("An error occurred while initializing the API");
                Logger.Error(e);
            }
        }

        /// <summary>
        /// Stop the background api
        /// </summary>
        public void Stop()
        {
            __running = false;
        }
    }

    /// <summary>
    /// The background api builder
    /// </summary>
    public class BackgroundApi : NancyModule
    {
        public static event EventHandler<EventArgs>? OnOpenWindow;
        public static event EventHandler<EventArgs>? OnOpenUpdatesPage;
        public static event EventHandler<KeyValuePair<string, string>>? OnShowSharedPackage;
        public static event EventHandler<EventArgs>? OnUpgradeAll;
        public static event EventHandler<string>? OnUpgradeAllForManager;
        public static event EventHandler<string>? OnUpgradePackage;

        public BackgroundApi()
        {
            // Enable CORS
            After.AddItemToEndOfPipeline((ctx) =>
            {
                ctx.Response.WithHeader("Access-Control-Allow-Origin", "*")
                    .WithHeader("Access-Control-Allow-Methods", "POST,GET,OPTIONS")
                    .WithHeader("Access-Control-Allow-Headers", "Authorization");
            });
            BuildShareApi();
            BuildV1WidgetsApi();
        }

        /// <summary>
        /// Build the endpoints required for the Share Interface
        /// </summary>
        public void BuildShareApi()
        {
            // Show package from https://marticliment.com/unigetui/share
            Get("/v2/show-package", (_) =>
            {
                try
                {
                    if (Request.Query.@pid == "" || Request.Query.@psource == "")
                    {
                        return 400;
                    }

                    OnShowSharedPackage?.Invoke(this, new KeyValuePair<string, string>(Request.Query.@pid.ToString(), Request.Query.@psource.ToString()));

                    return "{\"status\": \"success\"}";
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                    return 500;
                }
            });

            // Basic entrypoint to know if UniGetUI is running
            Get("/is-running", (_) =>
            {
                return "{\"status\": \"success\"}";
            });
        }

        /// <summary>
        /// Build the endpoints required for the /widgets/v1 endpoint. All of these
        /// endpoints are authenticated with MainApp.Instance.AuthenticateToken
        /// </summary>
        public void BuildV1WidgetsApi()
        {
            // Basic version check
            Get("/widgets/v1/get_wingetui_version", (_) =>
            {
                if (!BackgroundApiRunner.AuthenticateToken(Request.Query.@token))
                {
                    return 401;
                }

                return CoreData.BuildNumber.ToString();
            });

            // Return found updates
            Get("/widgets/v1/get_updates", async (parameters) =>
            {
                if (!BackgroundApiRunner.AuthenticateToken(Request.Query.@token))
                {
                    return 401;
                }

                if (!PEInterface.UpgradablePackagesLoader.IsLoaded && !PEInterface.UpgradablePackagesLoader.IsLoading)
                {
                    _ = PEInterface.UpgradablePackagesLoader.ReloadPackages(); // Actually begin loading the updates if not loaded or loading
                }

                while (PEInterface.UpgradablePackagesLoader.IsLoading)
                {
                    await Task.Delay(100); // Wait for the updates to be reported before showing anything
                }

                StringBuilder packages = new();
                foreach (IPackage package in PEInterface.UpgradablePackagesLoader.Packages)
                {
                    if (package.Tag is PackageTag.OnQueue or PackageTag.BeingProcessed)
                        continue; // Do not show already processed packages on queue

                    string icon = $"http://localhost:7058/widgets/v2/get_icon_for_package?packageId={HttpUtility.UrlEncode(package.Id)}&packageSource={HttpUtility.UrlEncode(package.Source.Name)}&token={ApiTokenHolder.Token}";
                    packages.Append($"{package.Name.Replace('|', '-')}|{package.Id}|{package.VersionString}|{package.NewVersionString}|{package.Source.AsString_DisplayName}|{package.Manager.Name}|{icon}&&");
                }

                string pkgs_ = packages.ToString();

                if (pkgs_.Length > 2)
                {
                    pkgs_ = pkgs_[..(pkgs_.Length - 2)];
                }

                return pkgs_;
            });

            // Open UniGetUI (as it was)
            Get("/widgets/v1/open_wingetui", (_) =>
            {
                if (!BackgroundApiRunner.AuthenticateToken(Request.Query.@token))
                {
                    return 401;
                }

                OnOpenWindow?.Invoke(this, EventArgs.Empty);
                return 200;
            });

            // Open UniGetUI with the Updates page shown
            Get("/widgets/v1/view_on_wingetui", (_) =>
            {
                if (!BackgroundApiRunner.AuthenticateToken(Request.Query.@token))
                {
                    return 401;
                }

                OnOpenUpdatesPage?.Invoke(this, EventArgs.Empty);
                return 200;
            });

            // Update a specific package given its Id
            Get("/widgets/v1/update_package", (_) =>
            {
                if (!BackgroundApiRunner.AuthenticateToken(Request.Query.@token))
                {
                    return 401;
                }

                if (Request.Query.@id == "")
                {
                    return 400;
                }

                OnUpgradePackage?.Invoke(this, Request.Query.@id);
                return 200;
            });

            // Update all packages
            Get("/widgets/v1/update_all_packages", (_) =>
            {
                if (!BackgroundApiRunner.AuthenticateToken(Request.Query.@token))
                {
                    return 401;
                }

                Logger.Info("[WIDGETS] Updating all packages");
                OnUpgradeAll?.Invoke(this, EventArgs.Empty);
                return 200;
            });

            // Update all packages for a specific manager
            Get("/widgets/v1/update_all_packages_for_source", (_) =>
            {
                if (!BackgroundApiRunner.AuthenticateToken(Request.Query.@token))
                {
                    return 401;
                }

                if (Request.Query.@source == "")
                {
                    return 400;
                }

                Logger.Info($"[WIDGETS] Updating all packages for manager {Request.Query.@source}");
                OnUpgradeAllForManager?.Invoke(this, Request.Query.@source);
                return 200;
            });

            // Update all packages for a specific manager
            Get("/widgets/v2/get_icon_for_package", async (_) =>
            {
                if (!BackgroundApiRunner.AuthenticateToken(Request.Query.@token))
                {
                    return 401;
                }

                if (Request.Query.@packageId == "" || Request.Query.@packageSource == "")
                {
                    return 400;
                }

                string iconPath = Path.Join(CoreData.UniGetUIExecutableDirectory, "Assets", "Images", "package_color.png");
                IPackage? package = PEInterface.UpgradablePackagesLoader.GetPackageForId(Request.Query.@packageId, Request.Query.@packageSource);
                if (package is not null)
                {
                    Uri iconUrl = await Task.Run(package.GetIconUrl);
                    if (iconUrl.ToString() != "ms-appx:///Assets/Images/package_color.png")
                    {
                        string mimePath = Path.Join(CoreData.UniGetUICacheDirectory_Icons, package.Manager.Name,
                            package.Id, "icon.mime");
                        iconPath = Path.Join(CoreData.UniGetUICacheDirectory_Icons, package.Manager.Name, package.Id,
                            $"icon.{IconCacheEngine.MimeToExtension[await File.ReadAllTextAsync(mimePath)]}");
                    }
                    // else, the iconPath will be the preloaded one (package_color.png)
                }
                else
                {
                    Logger.Warn($"[API] Package id={Request.Query.@packageId} with sourceName={Request.Query.@packageSource} was not found!");
                }

                byte[] fileContents = await File.ReadAllBytesAsync(iconPath);
                return new Response
                {
                    ContentType = IconCacheEngine.ExtensionToMime[iconPath.Split('.')[^1]],
                    Contents = (stream) =>
                    {
                        try
                        {
                            stream.Write(fileContents, 0, fileContents.Length);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"Unable to load icon to path {iconPath}");
                            Logger.Warn(ex);
                        }
                    }
                };

            });
        }
    }
}

