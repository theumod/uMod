extern alias References;

using Oxide.Core.Plugins;
using References::ProtoBuf;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;

namespace Oxide.Core.Libraries
{
    /// <summary>
    /// Contains all data for a specified user
    /// </summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
    public class UserData
    {
        /// <summary>
        /// Gets or sets the last seen nickname for this player
        /// </summary>
        public string LastSeenNickname { get; set; } = "Unnamed";

        /// <summary>
        /// Gets or sets the individual permissions for this player
        /// </summary>
        public HashSet<string> Perms { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets or sets the group for this player
        /// </summary>
        public HashSet<string> Groups { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Contains all data for a specified group
    /// </summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
    public class GroupData
    {
        /// <summary>
        /// Gets or sets the title of this group
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the rank of this group
        /// </summary>
        public int Rank { get; set; }

        /// <summary>
        /// Gets or sets the individual permissions for this group
        /// </summary>
        public HashSet<string> Perms { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets or sets the parent for this group
        /// </summary>
        public string ParentGroup { get; set; } = string.Empty;
    }

    /// <summary>
    /// A library providing a unified permissions system
    /// </summary>
    public class Permission : Library
    {
        /// <summary>
        /// Returns if this library should be loaded into the global namespace
        /// </summary>
        public override bool IsGlobal => false;

        // All registered permissions
        private readonly Dictionary<Plugin, HashSet<string>> registeredPermissions;

        // All users data
        private Dictionary<string, UserData> usersData;

        // All groups data
        private Dictionary<string, GroupData> groupsData;

        private Func<string, bool> validate;

        // Permission status
        public bool IsLoaded { get; private set; }

        /// <summary>
        /// Initializes a new instance of the Permission library
        /// </summary>
        public Permission()
        {
            // Initialize
            registeredPermissions = new Dictionary<Plugin, HashSet<string>>();

            // Load the datafile
            LoadFromDatafile();
        }

        /// <summary>
        /// Loads all permissions data from the datafile
        /// </summary>
        private void LoadFromDatafile()
        {
            VerifyAndLoadUsersData();
            VerifyAndLoadGroupsData();

            bool circularReference = false;
            foreach (KeyValuePair<string, GroupData> pair in groupsData)
            {
                if (string.IsNullOrEmpty(pair.Value.ParentGroup) || !HasCircularParent(pair.Key, pair.Value.ParentGroup))
                {
                    continue;
                }

                Interface.Oxide.LogWarning("Detected circular parent group for '{0}'; removing parent '{1}'", pair.Key, pair.Value.ParentGroup);
                pair.Value.ParentGroup = null;
                circularReference = true;
            }

            if (circularReference)
            {
                SaveGroups();
            }

            IsLoaded = true;
        }

        private void VerifyAndLoadUsersData()
        {
            Utility.DatafileToProto<Dictionary<string, UserData>>("oxide.users");

            Dictionary<string, UserData> storedUserData = ProtoStorage.Load<Dictionary<string, UserData>>("oxide.users") ?? new Dictionary<string, UserData>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, UserData> result = new Dictionary<string, UserData>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            bool changed = false;

            foreach (KeyValuePair<string, UserData> entry in storedUserData)
            {
                UserData user = entry.Value;

                permissions.Clear();
                groups.Clear();

                foreach (string perm in user.Perms)
                {
                    permissions.Add(perm);
                }

                user.Perms = new HashSet<string>(permissions, StringComparer.OrdinalIgnoreCase);

                foreach (string group in user.Groups)
                {
                    groups.Add(group);
                }

                user.Groups = new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);

                if (result.ContainsKey(entry.Key))
                {
                    UserData existing = result[entry.Key];
                    existing.Perms.UnionWith(user.Perms);
                    existing.Groups.UnionWith(user.Groups);

                    changed = true;

                    continue;
                }

                result.Add(entry.Key, user);
            }

            usersData = result;
            if (changed)
            {
                SaveUsers();
            }
        }

        private void VerifyAndLoadGroupsData()
        {
            Utility.DatafileToProto<Dictionary<string, GroupData>>("oxide.groups");

            Dictionary<string, GroupData> storedGroupData = ProtoStorage.Load<Dictionary<string, GroupData>>("oxide.groups") ?? new Dictionary<string, GroupData>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, GroupData> result = new Dictionary<string, GroupData>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            bool changed = false;

            foreach (KeyValuePair<string, GroupData> entry in storedGroupData)
            {
                GroupData group = entry.Value;

                permissions.Clear();

                foreach (string perm in group.Perms)
                {
                    permissions.Add(perm);
                }

                group.Perms = new HashSet<string>(permissions, StringComparer.OrdinalIgnoreCase);

                if (result.ContainsKey(entry.Key))
                {
                    GroupData existing = result[entry.Key];
                    existing.Perms.UnionWith(group.Perms);
                    changed = true;

                    continue;
                }

                result.Add(entry.Key, group);
            }

            groupsData = result;
            if (changed)
            {
                SaveGroups();
            }
        }

        private Dictionary<string, GroupData> VerifyGroupData(Dictionary<string, GroupData> data)
        {
            Dictionary<string, GroupData> result = new Dictionary<string, GroupData>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, GroupData> entry in data)
            {
                GroupData group = entry.Value;

                permissions.Clear();

                foreach (string perm in group.Perms)
                {
                    permissions.Add(perm);
                }

                group.Perms = new HashSet<string>(permissions, StringComparer.OrdinalIgnoreCase);

                if (result.ContainsKey(entry.Key))
                {
                    GroupData existing = result[entry.Key];
                    // Get a matching entry.key from result.keys
                    var existingKey = result.Keys.FirstOrDefault(x => x.Equals(entry.Key, StringComparison.OrdinalIgnoreCase));

                    existing.Perms.UnionWith(group.Perms);

                    Interface.Oxide.LogWarning("Duplicate group '{0}' found, merged entries with {1}", entry.Key, existingKey);

                    continue;
                }

                result.Add(entry.Key, group);
            }

            return result;
        }

        /// <summary>
        /// Exports user/group data to json
        /// </summary>
        [LibraryFunction("Export")]
        public void Export(string prefix = "auth")
        {
            if (IsLoaded)
            {
                Interface.Oxide.DataFileSystem.WriteObject(prefix + ".groups", groupsData);
                Interface.Oxide.DataFileSystem.WriteObject(prefix + ".users", usersData);
            }
        }

        /// <summary>
        /// Saves all permissions data to the data files
        /// </summary>
        public void SaveData()
        {
            SaveUsers();
            SaveGroups();
        }

        /// <summary>
        /// Saves users permissions data to the data file
        /// </summary>
        public void SaveUsers() => ProtoStorage.Save(usersData, "oxide.users");

        /// <summary>
        /// Saves groups permissions data to the data file
        /// </summary>
        public void SaveGroups() => ProtoStorage.Save(groupsData, "oxide.groups");

        /// <summary>
        /// Register user ID validation
        /// </summary>
        /// <param name="val"></param>
        public void RegisterValidate(Func<string, bool> val) => validate = val;

        /// <summary>
        /// Clean invalid user ID entries
        /// </summary>
        public void CleanUp()
        {
            if (IsLoaded && validate != null)
            {
                string[] invalidData = usersData.Keys.Where(i => !validate(i)).ToArray();
                if (invalidData.Length <= 0)
                {
                    return;
                }

                foreach (string i in invalidData)
                {
                    usersData.Remove(i);
                }
            }
        }

        /// <summary>
        /// Migrate permissions from one group to another
        /// </summary>
        public void MigrateGroup(string oldGroupName, string newGroupName)
        {
            if (IsLoaded && GroupExists(oldGroupName))
            {
                string groups = ProtoStorage.GetFileDataPath("oxide.groups.data");
                File.Copy(groups, groups + ".old", true);

                foreach (string permission in GetGroupPermissions(oldGroupName))
                {
                    GrantGroupPermission(newGroupName, permission, null);
                }

                if (GetUsersInGroup(oldGroupName).Length == 0)
                {
                    RemoveGroup(oldGroupName);
                }
            }
        }

        #region Permission Management

        /// <summary>
        /// Registers the specified permission
        /// </summary>
        /// <param name="permission"></param>
        /// <param name="owner"></param>
        [LibraryFunction("RegisterPermission")]
        public void RegisterPermission(string permission, Plugin owner)
        {
            if (string.IsNullOrEmpty(permission))
            {
                return;
            }

            if (PermissionExists(permission))
            {
                Interface.Oxide.LogWarning("Duplicate permission registered '{0}' (by plugin '{1}')", permission, owner.Title);
                return;
            }

            if (!registeredPermissions.TryGetValue(owner, out HashSet<string> set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                registeredPermissions.Add(owner, set);
                owner.OnRemovedFromManager.Add(owner_OnRemovedFromManager);
            }
            set.Add(permission);

            Interface.CallHook("OnPermissionRegistered", permission, owner);

            if (!permission.StartsWith($"{owner.Name}.", StringComparison.OrdinalIgnoreCase) && !owner.IsCorePlugin)
            {
                Interface.Oxide.LogWarning("Missing plugin name prefix '{0}' for permission '{1}' (by plugin '{2}')", owner.Name.ToLower(), permission, owner.Title);
            }
        }

        /// <summary>
        /// Returns if the specified permission exists or not
        /// </summary>
        /// <param name="permission"></param>
        /// <param name="owner"></param>
        /// <returns></returns>
        [LibraryFunction("PermissionExists")]
        public bool PermissionExists(string permission, Plugin owner = null)
        {
            if (string.IsNullOrEmpty(permission))
            {
                return false;
            }

            if (owner == null)
            {
                if (registeredPermissions.Count > 0)
                {
                    if (permission.Equals("*"))
                    {
                        return true;
                    }

                    if (permission.EndsWith("*"))
                    {
                        return registeredPermissions.Values.SelectMany(v => v).Any(p => p.StartsWith(permission.TrimEnd('*'), StringComparison.OrdinalIgnoreCase));
                    }
                }
                return registeredPermissions.Values.Any(v => v.Contains(permission, StringComparer.OrdinalIgnoreCase));
            }

            if (!registeredPermissions.TryGetValue(owner, out HashSet<string> set))
            {
                return false;
            }

            if (set.Count > 0)
            {
                if (permission.Equals("*"))
                {
                    return true;
                }

                if (permission.EndsWith("*"))
                {
                    return set.Any(p => p.StartsWith(permission.TrimEnd('*'), StringComparison.OrdinalIgnoreCase));
                }
            }
            return set.Contains(permission, StringComparer.OrdinalIgnoreCase);
        }

        #endregion Permission Management

        /// <summary>
        /// Called when a plugin has been unloaded
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="manager"></param>
        private void owner_OnRemovedFromManager(Plugin sender, PluginManager manager) => registeredPermissions.Remove(sender);

        #region Querying

        /// <summary>
        /// Returns if the specified user id is valid
        /// </summary>
        /// <param name="playerId"></param>
        /// <returns></returns>
        [LibraryFunction("UserIdValid")]
        public bool UserIdValid(string playerId) => validate == null || validate(playerId);

        /// <summary>
        /// Returns if the specified user exists
        /// </summary>
        /// <param name="playerId"></param>
        /// <returns></returns>
        [LibraryFunction("UserExists")]
        public bool UserExists(string playerId) => usersData.ContainsKey(playerId);

        /// <summary>
        /// Returns the data for the specified user
        /// </summary>
        /// <param name="playerId"></param>
        /// <returns></returns>
        public UserData GetUserData(string playerId)
        {
            if (!usersData.TryGetValue(playerId, out UserData userData))
            {
                usersData.Add(playerId, userData = new UserData());
            }

            // Return the data
            return userData;
        }

        /// <summary>
        /// Updates the nickname
        /// </summary>
        /// <param name="playerId"></param>
        /// <param name="playerName"></param>
        [LibraryFunction("UpdateNickname")]
        public void UpdateNickname(string playerId, string playerName)
        {
            if (UserExists(playerId))
            {
                UserData userData = GetUserData(playerId);
                string oldName = userData.LastSeenNickname;
                string newName = playerName.Sanitize();
                userData.LastSeenNickname = playerName.Sanitize();

                Interface.CallHook("OnUserNameUpdated", playerId, oldName, newName);
            }
        }

        /// <summary>
        /// Check if user has a group
        /// </summary>
        /// <param name="playerId"></param>
        [LibraryFunction("UserHasAnyGroup")]
        public bool UserHasAnyGroup(string playerId) => UserExists(playerId) && GetUserData(playerId).Groups.Count > 0;

        /// <summary>
        /// Returns if the specified group has the specified permission or not
        /// </summary>
        /// <param name="groupNames"></param>
        /// <param name="permission"></param>
        /// <returns></returns>
        [LibraryFunction("GroupsHavePermission")]
        public bool GroupsHavePermission(HashSet<string> groupNames, string permission) => groupNames.Any(g => GroupHasPermission(g, permission));

        /// <summary>
        /// Returns if the specified group has the specified permission or not
        /// </summary>
        /// <param name="groupName"></param>
        /// <param name="permission"></param>
        /// <returns></returns>
        [LibraryFunction("GroupHasPermission")]
        public bool GroupHasPermission(string groupName, string permission)
        {
            if (!GroupExists(groupName) || string.IsNullOrEmpty(permission))
            {
                return false;
            }

            // Check if the group has the permission
            if (!groupsData.TryGetValue(groupName, out GroupData groupData))
            {
                return false;
            }

            return groupData.Perms.Contains(permission, StringComparer.OrdinalIgnoreCase) || GroupHasPermission(groupData.ParentGroup, permission);
        }

        /// <summary>
        /// Returns if the specified user has the specified permission
        /// </summary>
        /// <param name="playerId"></param>
        /// <param name="permission"></param>
        /// <returns></returns>
        [LibraryFunction("UserHasPermission")]
        public bool UserHasPermission(string playerId, string permission)
        {
            if (string.IsNullOrEmpty(permission))
            {
                return false;
            }

            // Always allow the server console
            if (playerId.Equals("server_console"))
            {
                return true;
            }

            // First, get the player data
            UserData userData = GetUserData(playerId);

            // Check if they have the permission
            if (userData.Perms.Contains(permission, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            // Check if their group has the permission
            return GroupsHavePermission(userData.Groups, permission);
        }

        /// <summary>
        /// Returns the group to which the specified user belongs
        /// </summary>
        /// <param name="playerId"></param>
        /// <returns></returns>
        [LibraryFunction("GetUserGroups")]
        public string[] GetUserGroups(string playerId) => GetUserData(playerId).Groups.ToArray();

        /// <summary>
        /// Returns the permissions which the specified user has
        /// </summary>
        /// <param name="playerId"></param>
        /// <returns></returns>
        [LibraryFunction("GetUserPermissions")]
        public string[] GetUserPermissions(string playerId)
        {
            UserData userData = GetUserData(playerId);
            HashSet<string> permissions = new HashSet<string>(userData.Perms, StringComparer.OrdinalIgnoreCase);

            foreach (string groupName in userData.Groups)
            {
                permissions.UnionWith(GetGroupPermissions(groupName));
            }

            return permissions.ToArray();
        }

        /// <summary>
        /// Returns the permissions which the specified group has, with optional transversing of parent groups
        /// </summary>
        /// <param name="groupName"></param>
        /// <param name="parents"></param>
        /// <returns></returns>
        [LibraryFunction("GetGroupPermissions")]
        public string[] GetGroupPermissions(string groupName, bool parents = false)
        {
            if (!GroupExists(groupName))
            {
                return new string[0];
            }

            if (!groupsData.TryGetValue(groupName, out GroupData groupData))
            {
                return new string[0];
            }

            HashSet<string> permissions = new HashSet<string>(groupData.Perms);

            if (parents)
            {
                permissions.UnionWith(GetGroupPermissions(groupData.ParentGroup));
            }

            return permissions.ToArray();
        }

        /// <summary>
        /// Returns the permissions which are registered
        /// </summary>
        /// <returns></returns>
        [LibraryFunction("GetPermissions")]
        public string[] GetPermissions() => new HashSet<string>(registeredPermissions.Values.SelectMany(v => v)).ToArray();

        /// <summary>
        /// Returns the players with given permission
        /// </summary>
        /// <param name="permission"></param>
        /// <returns></returns>
        [LibraryFunction("GetPermissionUsers")]
        public string[] GetPermissionUsers(string permission)
        {
            if (string.IsNullOrEmpty(permission))
            {
                return new string[0];
            }

            HashSet<string> permissionUsers = new HashSet<string>();

            foreach (KeyValuePair<string, UserData> data in usersData)
            {
                if (data.Value.Perms.Contains(permission, StringComparer.OrdinalIgnoreCase))
                {
                    permissionUsers.Add($"{data.Key}({data.Value.LastSeenNickname})");
                }
            }

            return permissionUsers.ToArray();
        }

        /// <summary>
        /// Returns the groups with given permission
        /// </summary>
        /// <param name="permission"></param>
        /// <returns></returns>
        [LibraryFunction("GetPermissionGroups")]
        public string[] GetPermissionGroups(string permission)
        {
            if (string.IsNullOrEmpty(permission))
            {
                return new string[0];
            }

            HashSet<string> permissionGroups = new HashSet<string>();

            foreach (KeyValuePair<string, GroupData> data in groupsData)
            {
                if (data.Value.Perms.Contains(permission, StringComparer.OrdinalIgnoreCase))
                {
                    permissionGroups.Add(data.Key);
                }
            }

            return permissionGroups.ToArray();
        }

        /// <summary>
        /// Set the group to which the specified user belongs
        /// </summary>
        /// <param name="playerId"></param>
        /// <param name="groupName"></param>
        /// <returns></returns>
        [LibraryFunction("AddUserGroup")]
        public void AddUserGroup(string playerId, string groupName)
        {
            if (!GroupExists(groupName))
            {
                return;
            }

            UserData userData = GetUserData(playerId);

            if (!groupName.EndsWith("*"))
            {
                // Add the permission
                if (userData.Groups.Add(groupName))
                {
                    // Call hook for plugins
                    Interface.Call("OnUserGroupAdded", playerId, groupName);
                    Interface.Call("OnUserGroupsAdded", playerId, new string[] { groupName });
                }
                return;
            }

            IEnumerable<string> scopedGroups = groupsData.Keys;
            if (groupName != "*")
            {
                string groupPrefix = groupName.TrimEnd('*');
                scopedGroups = scopedGroups.Where(g => g.StartsWith(groupPrefix, StringComparison.OrdinalIgnoreCase));
            }
            string[] addedGroups = scopedGroups.Where(g => userData.Groups.Add(g)).ToArray();
            if (addedGroups.Length > 0)
            {
                Interface.Call("OnUserGroupsAdded", playerId, addedGroups);
            }
        }

        /// <summary>
        /// Set the group to which the specified user belongs
        /// </summary>
        /// <param name="playerId"></param>
        /// <param name="groupName"></param>
        /// <returns></returns>
        [LibraryFunction("RemoveUserGroup")]
        public void RemoveUserGroup(string playerId, string groupName)
        {
            if (!GroupExists(groupName))
            {
                return;
            }

            UserData userData = GetUserData(playerId);
            if (userData.Groups.Count <= 0)
            {
                return;
            }

            if (!groupName.EndsWith("*"))
            {
                if (userData.Groups.Remove(groupName))
                {
                    // Call hook for plugins
                    Interface.Call("OnUserGroupRemoved", playerId, groupName);
                    Interface.Call("OnUserGroupsRemoved", playerId, new string[] { groupName });
                }
                return;
            }

            if (groupName == "*")
            {
                string[] removedGroups = userData.Groups.ToArray();
                userData.Groups.Clear();
                Interface.Call("OnUserGroupsRemoved", playerId, removedGroups);
            }
            else
            {
                string groupPrefix = groupName.TrimEnd('*');
                string[] removedGroups = userData.Groups.Where(g => g.StartsWith(groupPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (string g in removedGroups)
                {
                    userData.Perms.Remove(g);
                }
                Interface.Call("OnUserGroupsRemoved", playerId, removedGroups);
            }
        }

        /// <summary>
        /// Get if the player belongs to given group
        /// </summary>
        /// <param name="playerId"></param>
        /// <param name="groupName"></param>
        /// <returns></returns>
        [LibraryFunction("UserHasGroup")]
        public bool UserHasGroup(string playerId, string groupName)
        {
            if (!GroupExists(groupName))
            {
                return false;
            }

            return GetUserData(playerId).Groups.Contains(groupName, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns if the specified group exists or not
        /// </summary>
        /// <param name="groupName"></param>
        /// <returns></returns>
        [LibraryFunction("GroupExists")]
        public bool GroupExists(string groupName)
        {
            return !string.IsNullOrEmpty(groupName) && (groupName.EndsWith("*") || groupsData.ContainsKey(groupName));
        }

        /// <summary>
        /// Returns the data for the specified group
        /// </summary>
        /// <param name="groupName"></param>
        /// <returns></returns>
        public GroupData GetGroupData(string groupName)
        {
            if (!groupsData.TryGetValue(groupName, out GroupData groupData))
            {
                return null;
            }

            // Return the data
            return groupData;
        }

        /// <summary>
        /// Returns existing groups
        /// </summary>
        /// <returns></returns>
        [LibraryFunction("GetGroups")]
        public string[] GetGroups() => groupsData.Keys.ToArray();

        /// <summary>
        /// Returns users in that group
        /// </summary>
        /// <param name="groupName"></param>
        /// <returns></returns>
        [LibraryFunction("GetUsersInGroup")]
        public string[] GetUsersInGroup(string groupName)
        {
            if (!GroupExists(groupName))
            {
                return new string[0];
            }

            return usersData.Where(u => u.Value.Groups.Contains(groupName, StringComparer.OrdinalIgnoreCase)).Select(u => $"{u.Key} ({u.Value.LastSeenNickname})").ToArray();
        }

        /// <summary>
        /// Returns usersIDs in that group
        /// </summary>
        /// <param name="groupName"></param>
        /// <returns></returns>
        [LibraryFunction("GetUserIDsInGroup")]
        public string[] GetUserIDsInGroup(string groupName)
        {
            if (string.IsNullOrEmpty(groupName) || !groupsData.ContainsKey(groupName))
            {
                return new string[0];
            }

            return usersData.Where(u => u.Value.Groups.Contains(groupName, StringComparer.OrdinalIgnoreCase)).Select(u => u.Key).ToArray();
        }

        /// <summary>
        /// Returns the title of the specified group
        /// </summary>
        /// <param name="groupName"></param>
        [LibraryFunction("GetGroupTitle")]
        public string GetGroupTitle(string groupName)
        {
            if (!GroupExists(groupName))
            {
                return string.Empty;
            }

            // First, get the group data
            if (!groupsData.TryGetValue(groupName, out GroupData groupData))
            {
                return string.Empty;
            }

            // Return the group title
            return groupData.Title;
        }

        /// <summary>
        /// Returns the rank of the specified group
        /// </summary>
        /// <param name="groupName"></param>
        /// <returns></returns>
        [LibraryFunction("GetGroupRank")]
        public int GetGroupRank(string groupName)
        {
            if (!GroupExists(groupName))
            {
                return 0;
            }

            // First, get the group data
            if (!groupsData.TryGetValue(groupName, out GroupData groupData))
            {
                return 0;
            }

            // Return the group rank
            return groupData.Rank;
        }

        #endregion Querying

        #region User Permission

        /// <summary>
        /// Grants the specified permission to the specified user
        /// </summary>
        /// <param name="playerId"></param>
        /// <param name="permission"></param>
        /// <param name="owner"></param>
        [LibraryFunction("GrantUserPermission")]
        public void GrantUserPermission(string playerId, string permission, Plugin owner)
        {
            // Check it is even a permission
            if (!PermissionExists(permission, owner))
            {
                return;
            }

            // Get the player data
            UserData userData = GetUserData(playerId);

            if (!permission.EndsWith("*"))
            {
                // Add the permission
                if (userData.Perms.Add(permission))
                {
                    // Call hook for plugins
                    Interface.Call("OnUserPermissionGranted", playerId, permission);
                    Interface.Call("OnUserPermissionsGranted", playerId, new string[] { permission });
                }
                return;
            }
            HashSet<string> permissions;
            if (owner == null)
            {
                permissions = new HashSet<string>(registeredPermissions.Values.SelectMany(v => v));
            }
            else if (!registeredPermissions.TryGetValue(owner, out permissions))
            {
                return;
            }

            IEnumerable<string> scopedPermissions = permissions;
            if (permission != "*")
            {
                string permissionPrefix = permission.TrimEnd('*');
                scopedPermissions = scopedPermissions.Where(p => p.StartsWith(permissionPrefix, StringComparison.OrdinalIgnoreCase));
            }
            string[] grantedPermissions = scopedPermissions.Where(p => userData.Perms.Add(p)).ToArray();
            if (grantedPermissions.Length > 0)
            {
                Interface.Call("OnUserPermissionsGranted", playerId, grantedPermissions);
            }
        }

        /// <summary>
        /// Revokes the specified permission from the specified user
        /// </summary>
        /// <param name="playerId"></param>
        /// <param name="permission"></param>
        [LibraryFunction("RevokeUserPermission")]
        public void RevokeUserPermission(string playerId, string permission)
        {
            if (string.IsNullOrEmpty(permission))
            {
                return;
            }

            // Get the player data
            UserData userData = GetUserData(playerId);
            if (userData.Perms.Count <= 0)
            {
                return;
            }

            if (!permission.EndsWith("*"))
            {
                // Remove the permission
                if (userData.Perms.Remove(permission))
                {
                    // Call hook for plugins
                    Interface.Call("OnUserPermissionRevoked", playerId, permission);
                    Interface.Call("OnUserPermissionsRevoked", playerId, new string[] { permission });
                }
                return;
            }

            if (permission == "*")
            {
                string[] revokedPermissions = userData.Perms.ToArray();
                userData.Perms.Clear();
                Interface.Call("OnUserPermissionsRevoked", playerId, revokedPermissions);
            }
            else
            {
                string permissionPrefix = permission.TrimEnd('*');
                string[] revokedPermissions = userData.Perms.Where(p => p.StartsWith(permissionPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (string p in revokedPermissions)
                {
                    userData.Perms.Remove(p);
                }
                Interface.Call("OnUserPermissionsRevoked", playerId, revokedPermissions);
            }
        }

        #endregion User Permission

        #region Group Permission

        /// <summary>
        /// Grant the specified permission to the specified group
        /// </summary>
        /// <param name="groupName"></param>
        /// <param name="permission"></param>
        /// <param name="owner"></param>
        [LibraryFunction("GrantGroupPermission")]
        public void GrantGroupPermission(string groupName, string permission, Plugin owner)
        {
            // Check it is even a permission
            if (!PermissionExists(permission, owner) || string.IsNullOrEmpty(groupName) || !groupsData.TryGetValue(groupName, out GroupData groupData))
            {
                return;
            }

            if (!permission.EndsWith("*"))
            {
                // Add the permission
                if (groupData.Perms.Add(permission))
                {
                    // Call hook for plugins
                    Interface.Call("OnGroupPermissionGranted", groupName, permission);
                    Interface.Call("OnGroupPermissionsGranted", groupName, new string[] { permission });
                }
                return;
            }

            HashSet<string> permissions;
            if (owner == null)
            {
                permissions = new HashSet<string>(registeredPermissions.Values.SelectMany(v => v));
            }
            else if (!registeredPermissions.TryGetValue(owner, out permissions))
            {
                return;
            }

            IEnumerable<string> scopedPermissions = permissions;
            if (permission != "*")
            {
                string permissionPrefix = permission.TrimEnd('*');
                scopedPermissions = scopedPermissions.Where(p => p.StartsWith(permissionPrefix, StringComparison.OrdinalIgnoreCase));
            }
            string[] grantedPermissions = scopedPermissions.Where(p => groupData.Perms.Add(p)).ToArray();
            if (grantedPermissions.Length > 0)
            {
                Interface.Call("OnGroupPermissionsGranted", groupName, grantedPermissions);
            }
        }

        /// <summary>
        /// Revokes the specified permission from the specified user
        /// </summary>
        /// <param name="groupName"></param>
        /// <param name="permission"></param>
        [LibraryFunction("RevokeGroupPermission")]
        public void RevokeGroupPermission(string groupName, string permission)
        {
            if (string.IsNullOrEmpty(permission) || string.IsNullOrEmpty(groupName) || !groupsData.TryGetValue(groupName, out GroupData groupData) || groupData.Perms.Count <= 0)
            {
                return;
            }

            if (!permission.EndsWith("*"))
            {
                // Remove the permission
                if (groupData.Perms.Remove(permission))
                {
                    // Call hook for plugins
                    Interface.Call("OnGroupPermissionRevoked", groupName, permission);
                    Interface.Call("OnGroupPermissionsRevoked", groupName, new string[] { permission });
                }
                return;
            }

            if (permission == "*")
            {
                string[] revokedPermissions = groupData.Perms.ToArray();
                groupData.Perms.Clear();
                Interface.Call("OnGroupPermissionsRevoked", groupName, revokedPermissions);
            }
            else
            {
                string permissionPrefix = permission.TrimEnd('*');
                string[] revokedPermissions = groupData.Perms.Where(p => p.StartsWith(permissionPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (string p in revokedPermissions)
                {
                    groupData.Perms.Remove(p);
                }
                Interface.Call("OnGroupPermissionsRevoked", groupName, revokedPermissions);
            }
        }

        #endregion Group Permission

        #region Group Management

        /// <summary>
        /// Creates the specified group
        /// </summary>
        /// <param name="groupName"></param>
        /// <param name="groupTitle"></param>
        /// <param name="groupRank"></param>
        [LibraryFunction("CreateGroup")]
        public bool CreateGroup(string groupName, string groupTitle, int groupRank)
        {
            // Check if it already exists
            if (GroupExists(groupName) || string.IsNullOrEmpty(groupName))
            {
                return false;
            }

            // Create the data
            GroupData groupData = new GroupData { Title = groupTitle, Rank = groupRank };

            // Add the group
            groupsData.Add(groupName, groupData);

            Interface.CallHook("OnGroupCreated", groupName, groupTitle, groupRank);

            return true;
        }

        /// <summary>
        /// Removes the specified group
        /// </summary>
        /// <param name="groupName"></param>
        [LibraryFunction("RemoveGroup")]
        public bool RemoveGroup(string groupName)
        {
            // Check if it even exists
            if (!GroupExists(groupName))
            {
                return false;
            }

            // Remove the group
            bool removed = groupsData.Remove(groupName);
            if (removed)
            {
                // Set children to having no parent group
                foreach (GroupData child in groupsData.Values.Where(g => g.ParentGroup == groupName))
                {
                    child.ParentGroup = string.Empty;
                }
            }

            // Remove group from users
            bool changed = usersData.Values.Aggregate(false, (current, userData) => current | userData.Groups.Remove(groupName));

            if (changed)
            {
                SaveUsers();
            }

            if (removed)
            {
                Interface.CallHook("OnGroupDeleted", groupName);
            }

            return true;
        }

        /// <summary>
        /// Sets the title of the specified group
        /// </summary>
        /// <param name="groupName"></param>
        /// <param name="groupTitle"></param>
        [LibraryFunction("SetGroupTitle")]
        public bool SetGroupTitle(string groupName, string groupTitle)
        {
            if (!GroupExists(groupName))
            {
                return false;
            }

            // First, get the group data
            if (!groupsData.TryGetValue(groupName, out GroupData groupData))
            {
                return false;
            }

            // Change the title
            if (groupData.Title == groupTitle)
            {
                return true;
            }

            groupData.Title = groupTitle;

            Interface.CallHook("OnGroupTitleSet", groupName, groupTitle);

            return true;
        }

        /// <summary>
        /// Sets the rank of the specified group
        /// </summary>
        /// <param name="groupName"></param>
        /// <param name="groupRank"></param>
        [LibraryFunction("SetGroupRank")]
        public bool SetGroupRank(string groupName, int groupRank)
        {
            if (!GroupExists(groupName))
            {
                return false;
            }

            // First, get the group data
            if (!groupsData.TryGetValue(groupName, out GroupData groupData))
            {
                return false;
            }

            // Change the rank
            if (groupData.Rank == groupRank)
            {
                return true;
            }

            groupData.Rank = groupRank;

            Interface.CallHook("OnGroupRankSet", groupName, groupRank);

            return true;
        }

        /// <summary>
        /// Gets the parent of the specified group
        /// </summary>
        /// <param name="groupName"></param>
        [LibraryFunction("GetGroupParent")]
        public string GetGroupParent(string groupName)
        {
            if (!GroupExists(groupName))
            {
                return string.Empty;
            }

            return !groupsData.TryGetValue(groupName, out GroupData groupData) ? string.Empty : groupData.ParentGroup;
        }

        /// <summary>
        /// Sets the parent of the specified group
        /// </summary>
        /// <param name="groupName"></param>
        /// <param name="parentGroupName"></param>
        [LibraryFunction("SetGroupParent")]
        public bool SetGroupParent(string groupName, string parentGroupName)
        {
            if (!GroupExists(groupName))
            {
                return false;
            }

            // First, get the group data
            if (!groupsData.TryGetValue(groupName, out GroupData groupData))
            {
                return false;
            }

            if (string.IsNullOrEmpty(parentGroupName))
            {
                groupData.ParentGroup = null;
                return true;
            }

            if (!GroupExists(parentGroupName) || groupName.Equals(parentGroupName))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(groupData.ParentGroup) && groupData.ParentGroup.Equals(parentGroupName))
            {
                return true;
            }

            if (HasCircularParent(groupName, parentGroupName))
            {
                return false;
            }

            // Change the parent group
            groupData.ParentGroup = parentGroupName;

            Interface.CallHook("OnGroupParentSet", groupName, parentGroupName);

            return true;
        }

        private bool HasCircularParent(string groupName, string parentGroupName)
        {
            // Get parent data
            if (!groupsData.TryGetValue(parentGroupName, out GroupData parentGroupData))
            {
                return false;
            }

            HashSet<string> groupNames = new HashSet<string> { groupName, parentGroupName };

            // Check for circular reference
            while (!string.IsNullOrEmpty(parentGroupData.ParentGroup))
            {
                // Found itself?
                if (!groupNames.Add(parentGroupData.ParentGroup))
                {
                    return true;
                }

                // Get next parent
                if (!groupsData.TryGetValue(parentGroupData.ParentGroup, out parentGroupData))
                {
                    return false;
                }
            }

            return false;
        }

        #endregion Group Management
    }
}
