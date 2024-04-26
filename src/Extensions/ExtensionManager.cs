using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using Oxide.Core.Plugins.Watchers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Logging;
using Oxide.DependencyInjection;
using Oxide.Pooling;

namespace Oxide.Core.Extensions
{
    /// <summary>
    /// Responsible for managing all Oxide extensions
    /// </summary>
    public sealed class ExtensionManager
    {
        // All loaded extensions
        private IList<Extension> extensions;

        // The search patterns for extensions
        private const string extSearchPattern = "Oxide.*.dll";

        /// <summary>
        /// Gets the logger to which this extension manager writes
        /// </summary>
        public Logger Logger { get; private set; }

        // All registered plugin loaders
        private IList<PluginLoader> pluginloaders;

        // All registered libraries
        private IDictionary<string, Type> libraries;

        // All registered watchers
        private IList<PluginChangeWatcher> changewatchers;

        private IArrayPoolProvider<object> Pool { get; }

        /// <summary>
        /// Initializes a new instance of the ExtensionManager class
        /// </summary>
        public ExtensionManager(Logger logger, IArrayPoolProvider<object> pool)
        {
            // Initialize
            Logger = logger;
            extensions = new List<Extension>();
            pluginloaders = new List<PluginLoader>();
            libraries = new Dictionary<string, Type>()
            {
                ["Covalence"] = typeof(Covalence),
                ["Global"] = typeof(Global),
                ["Lang"] = typeof(Lang),
                ["Permission"] = typeof(Permission),
                ["Plugins"] = typeof(Libraries.Plugins),
                ["Time"] = typeof(Time),
                ["Timer"] = typeof(Timer),
                ["WebRequests"] = typeof(WebRequests)
            };
            changewatchers = new List<PluginChangeWatcher>();
            Pool = pool;
        }

        #region Registering

        /// <summary>
        /// Registers the specified plugin loader
        /// </summary>
        /// <param name="loader"></param>
        public void RegisterPluginLoader(PluginLoader loader) => pluginloaders.Add(loader);

        /// <summary>
        /// Gets all plugin loaders
        /// </summary>
        /// <returns></returns>
        public IEnumerable<PluginLoader> GetPluginLoaders() => pluginloaders;

        /// <summary>
        /// Registers the specified library
        /// </summary>
        /// <param name="name"></param>
        /// <param name="library"></param>
        [Obsolete("Use Interface.Oxide.Services.AddSingleton")]
        public void RegisterLibrary(string name, Library library)
        {
            if (libraries.ContainsKey(name))
            {
                Logger.Write(LogType.Error, "An extension tried to register an already registered library: {0}", name);
            }
            else
            {
                libraries[name] = library.GetType();
                Interface.Oxide.Services.AddSingleton(library.GetType(), library);
#if DEBUG
                Logger.Write(LogType.Debug, "Registered Library {0} : {1}", name, library.GetType());
#endif
            }
        }

        /// <summary>
        /// Gets all library names
        /// </summary>
        /// <returns></returns>
        public IEnumerable<string> GetLibraries() => libraries.Keys;

        /// <summary>
        /// Gets the library by the specified name
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public Library GetLibrary(string name)
        {
            return !libraries.TryGetValue(name, out Type lib) ? null : Interface.Services.GetService(lib) as Library;
        }

        /// <summary>
        /// Registers the specified watcher
        /// </summary>
        /// <param name="watcher"></param>
        public void RegisterPluginChangeWatcher(PluginChangeWatcher watcher) => changewatchers.Add(watcher);

        /// <summary>
        /// Gets all plugin change watchers
        /// </summary>
        /// <returns></returns>
        public IEnumerable<PluginChangeWatcher> GetPluginChangeWatchers() => changewatchers;

        #endregion Registering

        /// <summary>
        /// Loads the extension at the specified filename
        /// </summary>
        /// <param name="filename"></param>
        public void LoadExtension(string filename)
        {
            string name = Utility.GetFileNameWithoutExtension(filename);

            // Check if the extension is already loaded
            if (extensions.Any(x => x.Filename == filename))
            {
                Logger.Write(LogType.Error, $"Failed to load extension '{name}': extension already loaded.");
                return;
            }

            try
            {
                Assembly assembly = null;

                // Check if the assembly is already loaded
                foreach (Assembly loadedAssembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (loadedAssembly.GetName().Name != name)
                    {
                        continue;
                    }

                    assembly = loadedAssembly;
                    break;
                }

                if (assembly == null)
                {
                    // Read the assembly from file
                    byte[] data = File.ReadAllBytes(filename);

                    // Load the assembly
                    assembly = Assembly.Load(data);
                }

                // Search for a type that derives Extension
                Type extType = typeof(Extension);
                Type extensionType = null;
                foreach (Type type in assembly.GetExportedTypes())
                {
                    if (!extType.IsAssignableFrom(type))
                    {
                        continue;
                    }

                    extensionType = type;
                    break;
                }

                if (extensionType == null)
                {
                    Logger.Write(LogType.Error, "Failed to load extension {0} ({1})", name, "Specified assembly does not implement an Extension class");
                    return;
                }

                // Create and register the extension
                Extension extension = ActivationUtility.CreateInstance(Interface.Oxide.ServiceProvider, extensionType) as Extension;
                if (extension != null)
                {
                    /*if (!forced)
                    {
                        if (extension.IsCoreExtension || extension.IsGameExtension)
                        {
                            Logger.Write(LogType.Error, $"Failed to load extension '{name}': you may not hotload Core or Game extensions.");
                            return;
                        }

                        if (!extension.SupportsReloading)
                        {
                            Logger.Write(LogType.Error, $"Failed to load extension '{name}': this extension does not support reloading.");
                            return;
                        }
                    }*/

                    extension.Filename = filename;
                    ConfigureServices(extension, Interface.Oxide.Services);
                    extension.Load();
                    extensions.Add(extension);

                    // Log extension loaded
                    string version = extension.Version.ToString();

                    if (extension.Branch != "master")
                    {
                        version += $"@{extension.Branch}";
                    }

                    Logger.Write(LogType.Info, $"Loaded extension {extension.Name} v{version} by {extension.Author}");
                }
            }
            catch (Exception ex)
            {
                Logger.WriteException($"Failed to load extension {name}", ex);
                RemoteLogger.Exception($"Failed to load extension {name}", ex);
            }
        }

        /// <summary>
        /// Unloads the extension at the specified filename
        /// </summary>
        /// <param name="filename"></param>
        public void UnloadExtension(string filename)
        {
            string name = Utility.GetFileNameWithoutExtension(filename);

            // Find the extension
            Extension extension = extensions.SingleOrDefault(x => x.Filename == filename);
            if (extension == null)
            {
                Logger.Write(LogType.Error, $"Failed to unload extension '{name}': extension not loaded.");
                return;
            }

            // Check if it is a Core or Game extension
            if (extension.IsCoreExtension || extension.IsGameExtension)
            {
                Logger.Write(LogType.Error, $"Failed to unload extension '{name}': you may not unload Core or Game extensions.");
                return;
            }

            // Check if the extension supports reloading
            if (!extension.SupportsReloading)
            {
                Logger.Write(LogType.Error, $"Failed to unload extension '{name}': this extension doesn't support reloading.");
                return;
            }

            // TODO: Unload any plugins referenceing this extension

            // Unload it
            extension.Unload();
            extensions.Remove(extension);

            // Log extension unloaded
            Logger.Write(LogType.Info, $"Unloaded extension {extension.Name} v{extension.Version} by {extension.Author}");
        }

        /// <summary>
        /// Reloads the extension at the specified filename
        /// </summary>
        /// <param name="filename"></param>
        public void ReloadExtension(string filename)
        {
            string name = Utility.GetFileNameWithoutExtension(filename);

            // Find the extension
            Extension extension = extensions.SingleOrDefault(x => Utility.GetFileNameWithoutExtension(x.Filename) == name);

            // If the extension isn't already loaded, load it
            if (extension == null)
            {
                LoadExtension(filename);
                return;
            }

            // Check if it is a Core or Game extension
            if (extension.IsCoreExtension || extension.IsGameExtension)
            {
                Logger.Write(LogType.Error, $"Failed to unload extension '{name}': you may not unload Core or Game extensions.");
                return;
            }

            // Check if the extension supports reloading
            if (!extension.SupportsReloading)
            {
                Logger.Write(LogType.Error, $"Failed to reload extension '{name}': this extension doesn't support reloading.");
                return;
            }

            UnloadExtension(filename);

            LoadExtension(filename);
        }

        /// <summary>
        /// Loads all extensions in the given directory
        /// </summary>
        /// <param name="directory"></param>
        public void LoadAllExtensions(string directory)
        {
            List<string> foundCore = new List<string>();
            List<string> foundGame = new List<string>();
            List<string> foundOther = new List<string>();
            string[] ignoredExtensions = { "Oxide.Core.dll", "Oxide.References.dll", "Oxide.Common.dll" };
            string[] coreExtensions = {
                "Oxide.CSharp", "Oxide.JavaScript", "Oxide.Lua", "Oxide.MySql", "Oxide.Python", "Oxide.SQLite", "Oxide.Unity"
            };
            string[] gameExtensions = {
                "Oxide.Blackwake", "Oxide.Blockstorm", "Oxide.FortressCraft", "Oxide.FromTheDepths", "Oxide.GangBeasts", "Oxide.Hurtworld",
                "Oxide.InterstellarRift", "Oxide.MedievalEngineers", "Oxide.Nomad", "Oxide.PlanetExplorers", "Oxide.ReignOfKings", "Oxide.Rust",
                "Oxide.RustLegacy", "Oxide.SavageLands", "Oxide.SevenDaysToDie", "Oxide.SpaceEngineers", "Oxide.TheForest", "Oxide.Terraria",
                "Oxide.Unturned"
            };
            string[] foundExtensions = Directory.GetFiles(directory, extSearchPattern);

            foreach (string extPath in foundExtensions.Where(e => !ignoredExtensions.Contains(Path.GetFileName(e))))
            {
                if (extPath.Contains("Oxide.Core.") && Array.IndexOf(foundExtensions, extPath.Replace(".Core", "")) != -1)
                {
                    Cleanup.Add(extPath);
                    continue;
                }

                if (extPath.Contains("Oxide.Ext.") && Array.IndexOf(foundExtensions, extPath.Replace(".Ext", "")) != -1)
                {
                    Cleanup.Add(extPath);
                    continue;
                }

                if (extPath.Contains("Oxide.Game."))
                {
                    Cleanup.Add(extPath);
                    continue;
                }

                string fileName = Path.GetFileNameWithoutExtension(extPath);

                if (coreExtensions.Contains(fileName))
                {
                    foundCore.Add(extPath);
                }
                else if (gameExtensions.Contains(fileName))
                {
                    foundGame.Add(extPath);
                }
                else
                {
                    foundOther.Add(extPath);
                }
            }

            foreach (string extPath in foundCore)
            {
                LoadExtension(Path.Combine(directory, extPath));
            }

            foreach (string extPath in foundGame)
            {
                LoadExtension(Path.Combine(directory, extPath));
            }

            foreach (string extPath in foundOther)
            {
                LoadExtension(Path.Combine(directory, extPath));
            }

            foreach (Extension ext in extensions.ToArray())
            {
                try
                {
                    ext.OnModLoad();
                }
                catch (Exception ex)
                {
                    extensions.Remove(ext);
                    Logger.WriteException($"Failed OnModLoad extension {ext.Name} v{ext.Version}", ex);
                    RemoteLogger.Exception($"Failed OnModLoad extension {ext.Name} v{ext.Version}", ex);
                }
            }
        }

        private void ConfigureServices(Extension extension, IServiceCollection services)
        {
            if (services == null)
            {
                return;
            }

            MethodInfo configure = extension.GetType().GetMethod(nameof(ConfigureServices), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (configure == null)
            {
                return;
            }

            object[] call = Pool.Take(1);
            call[0] = services;
            try
            {
                configure.Invoke(extension, call);
            }
            catch (Exception e)
            {
                Logger.WriteException($"Failed to call {nameof(ConfigureServices)} on extension {extension.Name} v{extension.Version} by {extension.Author}", e);
            }
            finally
            {
                Pool.Return(call);
            }
        }

        /// <summary>
        /// Gets all currently loaded extensions
        /// </summary>
        /// <returns></returns>
        public IEnumerable<Extension> GetAllExtensions() => extensions;

        /// <summary>
        /// Returns if an extension by the given name is present
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public bool IsExtensionPresent(string name) => extensions.Any(e => e.Name == name);

        /// <summary>
        /// Returns if an extension of the given type is present
        /// </summary>
        /// <typeparam name="T">Extension type</typeparam>
        /// <returns></returns>
        public bool IsExtensionPresent<T>() where T : Extension => extensions.Any(e => e is T);

        /// <summary>
        /// Gets the extension by the given name
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public Extension GetExtension(string name)
        {
            try
            {
                return extensions.Single(e => e.Name == name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Gets the extension by the given type
        /// </summary>
        /// <typeparam name="T">Extension type</typeparam>
        /// <returns>Extension of type <typeparamref name="T"/> if it is present, otherwise <see langword="null"/></returns>
        public T GetExtension<T>() where T : Extension
        {
            return (T)extensions.FirstOrDefault(e => e is T);
        }
    }
}
