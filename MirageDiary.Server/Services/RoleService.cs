using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MirageDiary.Server.Data;
using MirageDiary.Server.Models;

namespace MirageDiary.Server.Services;

public class RoleService
{
    private readonly IConfiguration _config;
    private readonly AppDbContext _db;

    public RoleService(IConfiguration config, AppDbContext db)
    {
        _config = config;
        _db = db;
    }

    /// <summary>
    /// Checks role by User ID.
    /// Checks admins.json (Admins by ID) and database roles across linked accounts.
    /// </summary>
    public async Task<string> GetUserRoleAsync(
        string userId,
        string? username = null,
        string? firstName = null,
        string? lastName = null)
    {
        if (string.IsNullOrWhiteSpace(userId)) return "Student";

        string cleanUserId = userId.Trim();

        // 1. Check if user ID is in admins.json
        if (IsAdminInConfiguration(cleanUserId))
        {
            return "Admin";
        }

        // 2. Lookup primary user entity in DB
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == cleanUserId);

        // Check primary DB role
        if (user != null)
        {
            if (string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase))
                return "Admin";

            if (string.Equals(user.Role, "HeadTeacher", StringComparison.OrdinalIgnoreCase))
                return "HeadTeacher";
        }

        // 3. If this is a MAX user or linked user, check linked Telegram or OID/StudentId account
        if (!string.IsNullOrEmpty(cleanUserId))
        {
            // Check by LinkedUserId if present
            if (user != null && !string.IsNullOrEmpty(user.LinkedUserId))
            {
                if (IsAdminInConfiguration(user.LinkedUserId)) return "Admin";

                var linked = await _db.Users.FirstOrDefaultAsync(u => u.Id == user.LinkedUserId);
                if (linked != null)
                {
                    if (string.Equals(linked.Role, "Admin", StringComparison.OrdinalIgnoreCase) || IsAdminInConfiguration(linked.Id))
                        return "Admin";
                    if (string.Equals(linked.Role, "HeadTeacher", StringComparison.OrdinalIgnoreCase))
                        return "HeadTeacher";
                }
            }

            // Check if another account with matching Oid / StudentId is Admin or HeadTeacher
            if (user != null && (!string.IsNullOrEmpty(user.Oid) || !string.IsNullOrEmpty(user.StudentId)))
            {
                var peer = await _db.Users.FirstOrDefaultAsync(u =>
                    u.Id != cleanUserId &&
                    ((!string.IsNullOrEmpty(user.Oid) && u.Oid == user.Oid) ||
                     (!string.IsNullOrEmpty(user.StudentId) && u.StudentId == user.StudentId)));

                if (peer != null)
                {
                    if (IsAdminInConfiguration(peer.Id) || string.Equals(peer.Role, "Admin", StringComparison.OrdinalIgnoreCase))
                        return "Admin";
                    if (string.Equals(peer.Role, "HeadTeacher", StringComparison.OrdinalIgnoreCase))
                        return "HeadTeacher";
                }
            }
        }

        if (user != null && !string.IsNullOrEmpty(user.Role))
        {
            return user.Role;
        }

        return "Student";
    }

    private bool IsAdminInConfiguration(string userId)
    {
        var adminIds = _config.GetSection("Admins").Get<string[]>() ?? Array.Empty<string>();

        var allAdmins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in adminIds)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            allAdmins.Add(a.Trim());
        }

        // Direct ID match (e.g. "6333975602" or "298216106")
        if (allAdmins.Contains(userId)) return true;

        // If userId is formatted like "max_298216106"
        if (userId.StartsWith("max_", StringComparison.OrdinalIgnoreCase))
        {
            string numericPart = userId["max_".Length..];
            if (allAdmins.Contains(numericPart)) return true;
        }

        return false;
    }
}
