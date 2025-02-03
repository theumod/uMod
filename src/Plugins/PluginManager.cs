using Oxide.Core.Logging;
using System;
using System.Collections.Generic;
using Oxide.Pooling;

namespace Oxide.Core.Plugins
{
    public delegate void PluginEvent(Plugin plugin);

    /// <summary>
    /// Manages a set of plugins
    /// </summary>
    public sealed class PluginManager
    {
        private enum SubscriptionChangeType : byte
        {
            Subscribe = 0,
            Unsubscribe = 1
        }

        private struct SubscriptionChange
        {
            public Plugin Plugin { get; }

            public SubscriptionChangeType Change { get; }

            public int Index { get; }

            public SubscriptionChange(Plugin plugin, SubscriptionChangeType type, int subIndex = -1)
            {
                Plugin = plugin;
                Change = type;
                Index = subIndex;
            }
        }

        private class HookSubscriptions
        {
            public IList<Plugin> Plugins { get; }

            public int CallDepth { get; set; }

            public bool IsStandartHook { get; }

            public HashSet<Type> ExpectedTypes { get; }

            public Queue<SubscriptionChange> PendingChanges { get; }

            public HookSubscriptions(HashSet<Type> types = null)
            {
                Plugins = new List<Plugin>();
                CallDepth = 0;
                if (types != null)
                {
                    IsStandartHook = true;
                    ExpectedTypes = types;
                }
                PendingChanges = new Queue<SubscriptionChange>();
            }
        }

        /// <summary>
        /// Gets the logger to which this plugin manager writes
        /// </summary>
        public Logger Logger { get; private set; }

        /// <summary>
        /// Gets or sets the path for plugin configs
        /// </summary>
        public string ConfigPath { get; set; }

        /// <summary>
        /// Called when a plugin has been added
        /// </summary>
        public event PluginEvent OnPluginAdded;

        /// <summary>
        /// Called when a plugin has been removed
        /// </summary>
        public event PluginEvent OnPluginRemoved;

        // All loaded plugins
        private readonly IDictionary<string, Plugin> loadedPlugins;

        // All hook subscriptions
        private readonly IDictionary<string, HookSubscriptions> hookSubscriptions;

        // Stores the last time a deprecation warning was printed for a specific hook
        private readonly Dictionary<string, float> nextDeprecatedWarningAt = new Dictionary<string, float>();

        // Re-usable conflict list used for hook calls
        private readonly List<string> hookConflicts = new List<string>();

        // A list of hooks(where conflicts exist between plugins) with the time for the next conflict check
        private readonly Dictionary<string, float> nextHookConflictsCheckAt = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        private IArrayPoolProvider<object> ObjectPool { get; }

        // Dictionary of saved types with their associated hooks
        private readonly Dictionary<Type, HashSet<string>> expectedHookTypes = new Dictionary<Type, HashSet<string>>()
        {
            { typeof(bool), new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "OnSaveLoad", "OnEntityActiveCheck", "OnEntityDistanceCheck", "OnEntityFromOwnerCheck", "OnEntityVisibilityCheck",
                "OnAIBrainStateSwitch", "OnEyePosValidate", "OnFuelCheck", "OnHorseHitch", "OnItemCraft",
                "OnMagazineReload", "OnPurchaseItem", "OnVendingTransaction", "OnWireClear", "OnRackedWeaponMount"
            } },
            { typeof(int), new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "OnFuelGetAmount", "OnFuelUse", "OnInventoryItemsCount", "OnInventoryItemsTake", "OnItemResearched",
                "OnItemUse", "OnMaxStackable", "OnResearchCostDetermine"
            } },
            { typeof(float), new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "OnOvenTemperature", "OnExplosiveFuseSet"
            } }
        };

        /// <summary>
        /// Initializes a new instance of the PluginManager class
        /// </summary>
        public PluginManager(Logger logger)
        {
            // Initialize
            ObjectPool = Interface.Oxide.PoolFactory.GetArrayProvider<object>();
            loadedPlugins = new Dictionary<string, Plugin>();
            hookSubscriptions = new Dictionary<string, HookSubscriptions>();
            Logger = logger;

            // Safe addition of default types unknown to PluginManager
            AddDefaultHookTypes("Item, Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
                "OnItemSplit", "OnFuelItemCheck", "OnFindBurnable", "OnInventoryItemFind", "OnInventoryAmmoItemFind", "OnDispenserBonus", "OnQuarryConsumeFuel", "OnFishCatch");
            AddDefaultHookTypes("BuildingPrivlidge, Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null", "OnBuildingPrivilege");
            AddDefaultHookTypes("BaseCorpse, Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null", "OnCorpsePopulate");
            AddDefaultHookTypes("SleepingBag, Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null", "OnPlayerRespawn");
            AddDefaultHookTypes("BasePlayer+SpawnPoint, Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null", "OnFindSpawnPoint", "OnPlayerRespawn");
            AddDefaultHookTypes("ItemContainer+CanAcceptResult, Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null", "CanAcceptItem");

            string listType = System.Text.RegularExpressions.Regex.Replace(typeof(List<int>).AssemblyQualifiedName, @"\[\[.*?\]\]", "[[{0}]]");
            AddDefaultHookTypes(string.Format(listType, "Item, Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null"), "OnInventoryItemsFind");
            AddDefaultHookTypes(string.Format(listType, "UnityEngine.Vector3, UnityEngine.CoreModule, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null"), "OnBoatPathGenerate");

            void AddDefaultHookTypes(string typeName, params string[] hooks)
            {
                var type = Type.GetType(typeName);
                if (type != null)
                {
                    expectedHookTypes[type] = new HashSet<string>(hooks, StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        /// <summary>
        /// Adds a plugin to this manager
        /// </summary>
        /// <param name="plugin"></param>
        public bool AddPlugin(Plugin plugin)
        {
            if (loadedPlugins.ContainsKey(plugin.Name))
            {
                return false;
            }

            loadedPlugins.Add(plugin.Name, plugin);
            plugin.HandleAddedToManager(this);
            OnPluginAdded?.Invoke(plugin);
            return true;
        }

        /// <summary>
        /// Removes a plugin from this manager
        /// </summary>
        /// <param name="plugin"></param>
        /// <returns></returns>
        public bool RemovePlugin(Plugin plugin)
        {
            if (!loadedPlugins.ContainsKey(plugin.Name))
            {
                return false;
            }

            loadedPlugins.Remove(plugin.Name);

            lock (hookSubscriptions)
            {
                foreach (HookSubscriptions sub in hookSubscriptions.Values)
                {
                    if (sub.Plugins.Contains(plugin))
                    {
                        sub.Plugins.Remove(plugin);
                    }
                }
            }

            plugin.HandleRemovedFromManager(this);
            OnPluginRemoved?.Invoke(plugin);
            return true;
        }

        /// <summary>
        /// Gets a plugin by name
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public Plugin GetPlugin(string name)
        {
            return loadedPlugins.TryGetValue(name, out Plugin plugin) ? plugin : null;
        }

        /// <summary>
        /// Gets all plugins managed by this manager
        /// </summary>
        /// <returns></returns>
        public IEnumerable<Plugin> GetPlugins() => loadedPlugins.Values;

        /// <summary>
        /// Subscribes the specified plugin to the specified hook.
        /// If the plugin is already subscribed to the given hook and the index is >= 0,
        /// the plugin will be moved to the specified index in the subscription list.
        /// If the index is -1, the plugin will be appended to the end of the list.
        /// </summary>
        /// <param name="hookName">The hook to subscribe to</param>
        /// <param name="plugin">The plugin to subscribe</param>
        /// <param name="subIndex">The index in the subscription list where the plugin should be moved to, if >= 0. Default is -1(append to the end)</param>
        internal void SubscribeToHook(string hookName, Plugin plugin, int subIndex = -1)
        {
            if (!loadedPlugins.ContainsKey(plugin.Name) || !plugin.IsCorePlugin && (hookName.StartsWith("IOn") || hookName.StartsWith("ICan")))
            {
                return;
            }

            HookSubscriptions subscriptions;

            lock (hookSubscriptions)
            {
                if (!hookSubscriptions.TryGetValue(hookName, out subscriptions))
                {
                    hookSubscriptions[hookName] = subscriptions = new HookSubscriptions(GetHookTypes(hookName));
                    /*if (subscriptions.IsStandartHook)
                        Logger.Write(LogType.Debug, $"Hook '{hookName}' expect next types({subscriptions.ExpectedTypes.Count()}): {string.Join(", ", subscriptions.ExpectedTypes.Select(t => t.ToString()).ToArray())}");*/
                }
            }

            // Avoids modifying the plugin list while iterating over it during a hook call
            if (subscriptions.CallDepth > 0)
            {
                subscriptions.PendingChanges.Enqueue(new SubscriptionChange(plugin, SubscriptionChangeType.Subscribe, subIndex));
            }
            else
            {
                TryAddPluginToHook(subscriptions, plugin, subIndex);
            }
        }

        /// <summary>
        /// Unsubscribes the specified plugin from the specified hook
        /// </summary>
        /// <param name="hookName">The hook to unsubscribe from</param>
        /// <param name="plugin">The plugin to unsubscribe</param>
        internal void UnsubscribeFromHook(string hookName, Plugin plugin)
        {
            if (!loadedPlugins.ContainsKey(plugin.Name) || !plugin.IsCorePlugin && (hookName.StartsWith("IOn") || hookName.StartsWith("ICan")))
            {
                return;
            }

            HookSubscriptions sublist;

            lock (hookSubscriptions)
            {
                if (!hookSubscriptions.TryGetValue(hookName, out sublist))
                {
                    return;
                }
            }

            // Avoids modifying the plugin list while iterating over it during a hook call
            if (sublist.CallDepth > 0)
            {
                sublist.PendingChanges.Enqueue(new SubscriptionChange(plugin, SubscriptionChangeType.Unsubscribe));
                return;
            }

            sublist.Plugins.Remove(plugin);
            //Logger.Write(LogType.Debug, $"Plugin '{plugin.Name}' is unsubscribing from hook '{hook}'!");
        }

        /// <summary>
        /// Adding the expected type for the hook. Works only for standard hooks(On and Can)
        /// </summary>
        /// <param name="hookName">The name of the hook to which it needs to be added</param>
        /// <param name="type">The type that needs to be added</param>
        public void AddTypeToHook(string hookName, Type type)
        {
            // The type to add is null or the hook is non-standard(!On and !Can)
            if (type == null || (!hookName.StartsWith("On", StringComparison.OrdinalIgnoreCase) && !hookName.StartsWith("Can", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            // Adding type to the dictionary of Types
            if (!expectedHookTypes.TryGetValue(type, out var hooks))
            {
                expectedHookTypes[type] = hooks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            // Failed to add because it is already present in this list and no actions are needed, exiting
            if (!hooks.Add(hookName))
            {
                return;
            }

            HookSubscriptions subscriptions;

            lock (hookSubscriptions)
            {
                if (!hookSubscriptions.TryGetValue(hookName, out subscriptions))
                {
                    return;
                }
            }

            // Adding a Type to an existing subscribed hook
            if (subscriptions.IsStandartHook)
            {
                subscriptions.ExpectedTypes.Add(type);
            }
        }

        /// <summary>
        /// Calls a hook on all plugins of this manager
        /// </summary>
        /// <param name="hookName"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        public object CallHook(string hookName, params object[] args)
        {
            HookSubscriptions subscriptions;

            // Locate the sublist
            lock (hookSubscriptions)
            {
                if (!hookSubscriptions.TryGetValue(hookName, out subscriptions))
                {
                    return null;
                }
            }

            if (subscriptions.Plugins.Count == 0)
            {
                return null;
            }

            // Loop each item
            object[] values = ObjectPool.Take(subscriptions.Plugins.Count);

            object returnValue = null;// The value that will be returned, it will remain null if all plugins return null values
            object lastValue = null;// The last non-null value for non-standard(!On and !Can) hooks or valid types
            Plugin lastPlugin = null;// The last plugin with a non-null value for non-standard(!On and !Can) hooks or valid types
            int validCount = 0;// The count of non-null values for non-standard(!On and !Can) hooks or valid types

            subscriptions.CallDepth++;
            try
            {
                for (int i = 0; i < subscriptions.Plugins.Count; i++)
                {
                    Plugin plugin = subscriptions.Plugins[i];
                    // Call the hook
                    object value = plugin.CallHook(hookName, args);
                    if (value != null)
                    {
                        returnValue = value;// Assigning the last non-null value of ANY type

                        // Assigning non-null values for non-standard(!On and !Can) hooks or VALID types
                        if (!subscriptions.IsStandartHook || subscriptions.ExpectedTypes.Contains(value.GetType()))
                        {
                            values[i] = value;
                            lastValue = value;
                            lastPlugin = plugin;
                            validCount++;
                        }
                    }
                }

                // Assigning the last valid value to the return value in case the last iterated plugin returns an invalid value
                if (validCount > 0)
                {
                    returnValue = lastValue;
                }

                // The number of non-null values of non-standard(!On and !Can) or VALID types is greater than 0, the last hook conflict time was not found or has already passed
                // If a hook conflict is present, it won't go away quickly, and performing constant checks for such hooks is unnecessary and causes spam in the console, especially for hooks like CanBeTargeted
                if (validCount > 0 && (!nextHookConflictsCheckAt.TryGetValue(hookName, out float nextConflictAt) || nextConflictAt <= Interface.Oxide.Now))
                {
                    // Perform a hook conflict check
                    hookConflicts.Clear();
                    for (int i = 0; i < subscriptions.Plugins.Count; i++)
                    {
                        Plugin plugin = subscriptions.Plugins[i];
                        object value = values[i];
                        if (value == null)
                        {
                            continue;
                        }

                        if (value.GetType().IsValueType)
                        {
                            if (!values[i].Equals(lastValue))
                            {
                                hookConflicts.Add($"{plugin.Name} - {value} ({value.GetType().Name})");
                            }
                        }
                        else
                        {
                            if (values[i] != lastValue)
                            {
                                hookConflicts.Add($"{plugin.Name} - {value} ({value.GetType().Name})");
                            }
                        }
                    }

                    // The number of conflicting values is greater than 0
                    if (hookConflicts.Count > 0)
                    {
                        // Notify the log about it
                        hookConflicts.Add($"{lastPlugin.Name} ({lastValue} ({lastValue.GetType().Name}))");
                        Logger.Write(LogType.Warning, "Calling hook '{0}' resulted in a conflict between the following plugins: {1}", hookName, string.Join(", ", hookConflicts.ToArray()));

                        // Setting the time for which the hook conflict check will be ignored to avoid unnecessary checks and console spam
                        nextHookConflictsCheckAt[hookName] = Interface.Oxide.Now + 60f;
                    }
                }

                ObjectPool.Return(values);
            }
            finally
            {
                subscriptions.CallDepth--;
                if (subscriptions.CallDepth == 0)
                {
                    // ProcessHookChanges
                    while (subscriptions.PendingChanges.Count != 0)
                    {
                        SubscriptionChange change = subscriptions.PendingChanges.Dequeue();

                        if (change.Change == SubscriptionChangeType.Subscribe)
                        {
                            TryAddPluginToHook(subscriptions, change.Plugin, change.Index);
                        }
                        else
                        {
                            subscriptions.Plugins.Remove(change.Plugin);
                        }
                    }
                }
            }

            return returnValue;
        }

        /// <summary>
        /// Calls a hook on all plugins of this manager and prints a deprecation warning
        /// </summary>
        /// <param name="oldHook"></param>
        /// <param name="newHook"></param>
        /// <param name="expireDate"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        public object CallDeprecatedHook(string oldHook, string newHook, DateTime expireDate, params object[] args)
        {
            HookSubscriptions subscriptions;

            lock (hookSubscriptions)
            {
                if (!hookSubscriptions.TryGetValue(oldHook, out subscriptions))
                {
                    return null;
                }
            }


            if (subscriptions.Plugins.Count == 0)
            {
                return null;
            }

            float now = Interface.Oxide.Now;
            if (!nextDeprecatedWarningAt.TryGetValue(oldHook, out float nextWarningAt) || nextWarningAt <= now)
            {
                // TODO: Add better handling
                var plugin = subscriptions.Plugins[0];
                Interface.Oxide.LogWarning($"'{plugin.Name} v{plugin.Version}' is using deprecated hook '{oldHook}', which will stop working on {expireDate.ToString("D")}. Please contact the author '{plugin.Author}' to update to '{newHook}'");

                // Setting the time for which notifications about deprecated hooks will be ignored
                nextDeprecatedWarningAt[oldHook] = now + 3600f;
            }

            return CallHook(oldHook, args);
        }

        // Retrieving saved Types from the dictionary by hook name
        private HashSet<Type> GetHookTypes(string hookName)
        {
            // The hook doesn't start with On or Can, meaning it's not a standard hook, returning null
            if (!hookName.StartsWith("On", StringComparison.OrdinalIgnoreCase) && !hookName.StartsWith("Can", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var result = new HashSet<Type>();

            // The hook starts with Can, which means it's a boolean hook
            if (hookName.StartsWith("Can", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(typeof(bool));
            }

            foreach (var kvp in expectedHookTypes)
            {
                if (kvp.Value.Contains(hookName))
                {
                    result.Add(kvp.Key);
                }
            }
            return result;
        }

        // Plugin subscription to a hook, with the option to specify the queue in the list
        // The ability to specify the call order is mainly needed for API plugins, where hooks should be called before others, such as when a player connects
        private void TryAddPluginToHook(HookSubscriptions subscriptions, Plugin plugin, int subIndex)
        {
            //Logger.Write(LogType.Debug, $"Plugin '{plugin.Name}' is subscribing to hook '{hook}'!");
            int currentIndex = subscriptions.Plugins.IndexOf(plugin);
            if (currentIndex == -1 || (subIndex >= 0 && subIndex != currentIndex && subscriptions.Plugins.Remove(plugin)))
            {
                // If the current index is -1, it means the plugin is not in the list, so we simply add it depending on the specified index
                // Otherwise, if the subscriber has indicated a desired index(index >= 0), we move the plugin to the specified index
                if (subIndex < 0 || subIndex >= subscriptions.Plugins.Count)
                {
                    subscriptions.Plugins.Add(plugin);
                }
                else
                {
                    subscriptions.Plugins.Insert(subIndex, plugin);
                }
            }
        }
    }
}
