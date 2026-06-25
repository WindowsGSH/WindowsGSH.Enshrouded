using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Query;
using WindowsGSH.Core.Readiness;

namespace WindowsGSH.Modules.Enshrouded;

public sealed class EnshroudedModule :
    ManifestBackedGameServerModule,
    IModuleExistingServerImportCapability,
    IModuleQueryCapability,
    IModuleReadinessCapability
{
    private const string ExecutableName = "enshrouded_server.exe";
    private const string ConfigFileName = "enshrouded_server.json";
    private const string LogDirectoryName = "logs";
    private const int GeneratedPasswordSuffixLength = 8;
    private const int MaximumRolePasswordLength = 16;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public override ModuleCapabilities Capabilities => Manifest.ToCapabilities(
        supportsQuery: true,
        supportsRcon: false);

    public override ServerDisplayInfo GetDisplayInfo(ServerInstance instance)
    {
        return new ServerDisplayInfo(
            IpAddress: GetSetting(instance, "network.ip", "0.0.0.0"),
            Port: GetSetting(instance, "network.queryPort", "15637"),
            MaxPlayers: GetSetting(instance, "server.maxPlayers", "16"));
    }

    public override Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        return Task.FromResult(new InstallPlan(
            "steamcmd",
            $"+force_install_dir \"{instance.InstallPath}\" +login anonymous +app_update 2278520 validate +quit",
            instance.InstallPath,
            [
                "Enshrouded dedicated server Steam app: 2278520.",
                "WindowsGSH writes enshrouded_server.json before start.",
                "Players connect through the query port, which defaults to 15637."
            ]));
    }

    public override Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        var executable = Path.Combine(instance.InstallPath, ExecutableName);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = instance.InstallPath,
            Arguments = BuildLaunchArguments(instance),
            UseShellExecute = !ConsoleInputStrategyPolicy.UsesRedirectedStreams(Runtime),
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = ConsoleInputStrategyPolicy.UsesRedirectedStreams(Runtime),
            RedirectStandardOutput = ConsoleInputStrategyPolicy.UsesRedirectedStreams(Runtime),
            RedirectStandardError = ConsoleInputStrategyPolicy.UsesRedirectedStreams(Runtime)
        };

        return Task.FromResult(startInfo);
    }

    public override bool IsInstallValid(ServerInstance instance)
    {
        return File.Exists(Path.Combine(instance.InstallPath, ExecutableName));
    }

    public override string? GetConsoleLogPath(ServerInstance instance)
    {
        return Path.Combine(instance.InstallPath, LogDirectoryName);
    }

    protected override string BuildLaunchArguments(ServerInstance instance)
    {
        var args = new List<string>();

        if (GetBool(instance.Settings, "launch.log", true))
        {
            args.Add("-log");
        }

        args.Add($"-ip={QuoteArgument(GetSetting(instance, "network.ip", "0.0.0.0"))}");
        args.Add($"-queryPort={GetInt(instance.Settings, "network.queryPort", 15637)}");
        args.Add($"-slotCount={GetInt(instance.Settings, "server.maxPlayers", 16)}");
        args.Add($"-name={QuoteArgument(GetSetting(instance, "server.name", "Enshrouded Server"))}");

        var additionalArguments = SanitizeAdditionalArguments(GetSetting(instance, "server.additionalArguments", string.Empty));
        if (!string.IsNullOrWhiteSpace(additionalArguments))
        {
            args.Add(additionalArguments);
        }

        return string.Join(' ', args);
    }

    public override Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(
        ServerInstance instance,
        CancellationToken cancellationToken)
    {
        var config = LoadConfig(instance);
        var settings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        CopyString(config, settings, "name", "server.name");
        CopyString(config, settings, "ip", "network.ip");
        CopyNumber(config, settings, "queryPort", "network.queryPort");
        CopyNumber(config, settings, "slotCount", "server.maxPlayers");
        CopyString(config, settings, "voiceChatMode", "chat.voiceChatMode");
        CopyBool(config, settings, "enableVoiceChat", "chat.enableVoiceChat");
        CopyBool(config, settings, "enableTextChat", "chat.enableTextChat");
        CopyString(config, settings, "gameSettingsPreset", "game.preset");

        if (config["gameSettings"] is JsonObject gameSettings)
        {
            foreach (var field in GameSettingFields)
            {
                CopyNode(gameSettings, settings, field.JsonKey, field.SettingKey);
            }
        }

        foreach (var role in ReadRoles(config))
        {
            var prefix = "roles." + role.Name.ToLowerInvariant();
            settings[$"{prefix}.password"] = role.Password;
            settings[$"{prefix}.reservedSlots"] = role.ReservedSlots;
        }

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(settings);
    }

    public override Task WriteConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        var config = LoadConfigForWrite(instance);
        SetString(config, "name", GetSetting(instance, "server.name", "Enshrouded Server"));
        SetString(config, "saveDirectory", GetSetting(instance, "paths.saveDirectory", "./savegame"));
        SetString(config, "logDirectory", GetSetting(instance, "paths.logDirectory", "./logs"));
        SetString(config, "ip", GetSetting(instance, "network.ip", "0.0.0.0"));
        SetNumber(config, "queryPort", GetInt(instance.Settings, "network.queryPort", 15637));
        SetNumber(config, "slotCount", GetInt(instance.Settings, "server.maxPlayers", 16));
        EnsureArray(config, "tags");
        SetString(config, "voiceChatMode", GetSetting(instance, "chat.voiceChatMode", "Proximity"));
        SetBool(config, "enableVoiceChat", GetBool(instance.Settings, "chat.enableVoiceChat", false));
        SetBool(config, "enableTextChat", GetBool(instance.Settings, "chat.enableTextChat", false));
        SetString(config, "gameSettingsPreset", GetSetting(instance, "game.preset", "Default"));

        var gameSettings = EnsureObject(config, "gameSettings");
        foreach (var field in GameSettingFields)
        {
            SetGameSetting(gameSettings, instance.Settings, field);
        }

        var userGroups = BuildUserGroups(config, instance.Settings);
        var bannedAccounts = config["bannedAccounts"]?.DeepClone() as JsonArray ??
                             config["bans"]?.DeepClone() as JsonArray ??
                             new JsonArray();
        config.Remove("userGroups");
        config.Remove("bannedAccounts");
        config.Remove("bans");
        config["userGroups"] = userGroups;
        config["bannedAccounts"] = bannedAccounts;

        var configPath = GetConfigPath(instance);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, config.ToJsonString(JsonOptions) + Environment.NewLine, Utf8NoBom);
        return Task.CompletedTask;
    }

    public bool CanImport(string path)
    {
        return ResolveImportInstallPath(path) != null;
    }

    public async Task<ModuleExistingServerImportProbe> PreviewImportAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var installPath = ResolveImportInstallPath(path) ?? path;
        var settings = GetConfigFields().ToDictionary(
            field => field.Key,
            field => field.DefaultValue,
            StringComparer.OrdinalIgnoreCase);

        var probe = new ServerInstance(
            "enshrouded-import",
            Path.GetFileName(installPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            Id,
            installPath,
            installPath,
            GetConfigPath(installPath),
            settings);

        foreach (var pair in await ReadConfigFileSettingsAsync(probe, cancellationToken).ConfigureAwait(false))
        {
            settings[pair.Key] = pair.Value;
        }

        var warnings = new List<string>();
        if (!File.Exists(GetConfigPath(installPath)))
        {
            warnings.Add("enshrouded_server.json was not found. WindowsGSH will create one from module defaults on first start.");
        }

        return new ModuleExistingServerImportProbe(
            GetSetting(settings, "server.name", Path.GetFileName(installPath)),
            installPath,
            settings,
            warnings);
    }

    public async Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        var host = "127.0.0.1";
        var port = GetInt(instance.Settings, "network.queryPort", 15637);

        try
        {
            var info = await new SourceA2sClient().QueryInfoAsync(host, port, TimeSpan.FromSeconds(2), cancellationToken)
                .ConfigureAwait(false);
            return new QueryResult(
                ModuleServerStatus.Online,
                OnlinePlayers: info.Players,
                MaxPlayers: info.MaxPlayers,
                Version: info.Version,
                Map: info.Map,
                Game: info.Game,
                QueryDurationMilliseconds: info.QueryDurationMilliseconds,
                Players: info.PlayerRows,
                DetailMessage: info.DetailMessage,
                Protocol: "A2S",
                Message: $"A2S responded from {host}:{port}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new QueryResult(ModuleServerStatus.Offline, Message: $"A2S query to {host}:{port} timed out.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException)
        {
            return new QueryResult(ModuleServerStatus.Offline, Message: $"A2S query to {host}:{port} failed: {ex.Message}");
        }
    }

    public Task<IReadOnlyList<ReadinessCheckResult>> CheckReadinessAsync(
        ServerInstance instance,
        CancellationToken cancellationToken)
    {
        var checks = new List<ReadinessCheckResult>();
        var executablePath = Path.Combine(instance.InstallPath, ExecutableName);
        checks.Add(File.Exists(executablePath)
            ? ReadinessCheckResult.Pass("Enshrouded executable", $"Found: {executablePath}")
            : ReadinessCheckResult.Fail("Enshrouded executable", $"Missing {ExecutableName}. Run install/update with SteamCMD app 2278520."));

        var configPath = GetConfigPath(instance);
        checks.Add(File.Exists(configPath)
            ? ReadinessCheckResult.Pass("Enshrouded config", $"Found: {configPath}")
            : ReadinessCheckResult.Info("Enshrouded config", "enshrouded_server.json will be created before the server starts."));

        if (File.Exists(configPath) && !TryParseConfig(configPath, out var configError))
        {
            checks.Add(ReadinessCheckResult.Fail(
                "Enshrouded config JSON",
                $"enshrouded_server.json could not be parsed: {configError}"));
        }

        var slotCount = GetInt(instance.Settings, "server.maxPlayers", 16);
        if (slotCount is < 1 or > 16)
        {
            checks.Add(ReadinessCheckResult.Warning("Player slots", "Enshrouded supports slotCount values from 1 to 16."));
        }

        return Task.FromResult<IReadOnlyList<ReadinessCheckResult>>(checks);
    }

    private static JsonObject LoadConfig(ServerInstance instance)
    {
        return LoadConfig(instance.InstallPath);
    }

    private static JsonObject LoadConfig(string installPath)
    {
        var path = GetConfigPath(installPath);
        if (!File.Exists(path))
        {
            return CreateDefaultConfig();
        }

        return ParseConfigFile(path);
    }

    private static JsonObject LoadConfigForWrite(ServerInstance instance)
    {
        var path = GetConfigPath(instance);
        return File.Exists(path)
            ? ParseConfigFile(path)
            : CreateDefaultConfig();
    }

    private static JsonObject ParseConfigFile(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException($"Enshrouded config file root must be a JSON object: {path}");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Enshrouded config file is not valid JSON and was not modified: {path}",
                ex);
        }
    }

    private static bool TryParseConfig(string path, out string error)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject)
            {
                error = "Root value must be a JSON object.";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static JsonObject CreateDefaultConfig()
    {
        var config = new JsonObject
        {
            ["name"] = "Enshrouded Server",
            ["saveDirectory"] = "./savegame",
            ["logDirectory"] = "./logs",
            ["ip"] = "0.0.0.0",
            ["queryPort"] = 15637,
            ["slotCount"] = 16,
            ["tags"] = new JsonArray(),
            ["voiceChatMode"] = "Proximity",
            ["enableVoiceChat"] = false,
            ["enableTextChat"] = false,
            ["gameSettingsPreset"] = "Default",
            ["gameSettings"] = CreateDefaultGameSettings()
        };
        config["userGroups"] = BuildDefaultUserGroups();
        config["bannedAccounts"] = new JsonArray();
        return config;
    }

    private static JsonObject CreateDefaultGameSettings()
    {
        var settings = new JsonObject();
        foreach (var field in GameSettingFields)
        {
            settings[field.JsonKey] = field.DefaultValue switch
            {
                bool value => value,
                int value => value,
                long value => value,
                double value => value,
                string value => value,
                _ => null
            };
        }

        return settings;
    }

    private static JsonArray BuildDefaultUserGroups()
    {
        return
        [
            CreateRole("Admin", GenerateRolePassword("Admin"), true, true, true, true, true, 0),
            CreateRole("Friend", GenerateRolePassword("Friend"), false, true, true, true, false, 0)
        ];
    }

    private static JsonArray BuildUserGroups(JsonObject existingConfig, IReadOnlyDictionary<string, object?> settings)
    {
        var existingRoles = ReadRoleObjects(existingConfig);
        var knownRoles = new[]
        {
            ("Admin", true, true, true, true, true),
            ("Friend", false, true, true, true, false)
        };

        var updatedRoles = new JsonArray();
        foreach (var (name, canKickBan, canAccessInventories, canEditWorld, canEditBase, canExtendBase) in knownRoles)
        {
            updatedRoles.Add(BuildRole(
                settings,
                existingRoles,
                name,
                canKickBan,
                canAccessInventories,
                canEditWorld,
                canEditBase,
                canExtendBase));
        }

        foreach (var role in existingRoles.Values.Where(role => !knownRoles.Any(known =>
                     string.Equals(known.Item1, role.Name, StringComparison.OrdinalIgnoreCase))))
        {
            updatedRoles.Add(role.Json.DeepClone());
        }

        return updatedRoles;
    }

    private static JsonObject BuildRole(
        IReadOnlyDictionary<string, object?> settings,
        IReadOnlyDictionary<string, RoleObject> existingRoles,
        string name,
        bool canKickBan,
        bool canAccessInventories,
        bool canEditWorld,
        bool canEditBase,
        bool canExtendBase)
    {
        var key = "roles." + name.ToLowerInvariant();
        var role = existingRoles.TryGetValue(name, out var existing)
            ? (JsonObject)existing.Json.DeepClone()
            : CreateRole(name, string.Empty, canKickBan, canAccessInventories, canEditWorld, canEditBase, canExtendBase, 0);
        var existingPassword = existingRoles.TryGetValue(name, out existing)
            ? existing.Password
            : string.Empty;
        var password = GetSetting(settings, key + ".password", existingPassword);
        if (string.IsNullOrWhiteSpace(password))
        {
            password = GenerateRolePassword(name);
        }
        else if (LooksLikeLegacyGeneratedPassword(name, password))
        {
            password = GenerateRolePassword(name);
        }

        var reservedSlots = GetInt(settings, key + ".reservedSlots", existing?.ReservedSlots ?? 0);
        role["name"] = name;
        role["password"] = password;
        role["reservedSlots"] = reservedSlots;

        if (!existingRoles.ContainsKey(name))
        {
            role["canKickBan"] = canKickBan;
            role["canAccessInventories"] = canAccessInventories;
            role["canEditWorld"] = canEditWorld;
            role["canEditBase"] = canEditBase;
            role["canExtendBase"] = canExtendBase;
        }

        return role;
    }

    private static JsonObject CreateRole(
        string name,
        string password,
        bool canKickBan,
        bool canAccessInventories,
        bool canEditWorld,
        bool canEditBase,
        bool canExtendBase,
        int reservedSlots)
    {
        return new JsonObject
        {
            ["name"] = name,
            ["password"] = password,
            ["canKickBan"] = canKickBan,
            ["canAccessInventories"] = canAccessInventories,
            ["canEditWorld"] = canEditWorld,
            ["canEditBase"] = canEditBase,
            ["canExtendBase"] = canExtendBase,
            ["reservedSlots"] = reservedSlots
        };
    }

    private static IReadOnlyList<RoleSettings> ReadRoles(JsonObject config)
    {
        return ReadRoleObjects(config).Values
            .Select(role => new RoleSettings(role.Name, role.Password, role.ReservedSlots))
            .ToArray();
    }

    private static Dictionary<string, RoleObject> ReadRoleObjects(JsonObject config)
    {
        if (config["userGroups"] is not JsonArray roles)
        {
            return [];
        }

        return roles
            .OfType<JsonObject>()
            .Select(role => new RoleObject(
                GetNodeString(role, "name", string.Empty),
                GetNodeString(role, "password", string.Empty),
                GetNodeInt(role, "reservedSlots", 0),
                role))
            .Where(role => !string.IsNullOrWhiteSpace(role.Name))
            .GroupBy(role => role.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    private static void SetGameSetting(JsonObject gameSettings, IReadOnlyDictionary<string, object?> settings, GameSettingField field)
    {
        if (!settings.TryGetValue(field.SettingKey, out var value) || value == null || string.IsNullOrWhiteSpace(value.ToString()))
        {
            value = field.DefaultValue;
        }

        gameSettings[field.JsonKey] = field.Kind switch
        {
            GameSettingKind.Boolean => ToBool(value, Convert.ToBoolean(field.DefaultValue, CultureInfo.InvariantCulture)),
            GameSettingKind.Integer => ToLong(value, Convert.ToInt64(field.DefaultValue, CultureInfo.InvariantCulture)),
            GameSettingKind.Number => ToDouble(value, Convert.ToDouble(field.DefaultValue, CultureInfo.InvariantCulture)),
            _ => value.ToString()
        };
    }

    private static JsonObject EnsureObject(JsonObject config, string key)
    {
        if (config[key] is JsonObject obj)
        {
            return obj;
        }

        obj = new JsonObject();
        config[key] = obj;
        return obj;
    }

    private static void EnsureArray(JsonObject config, string key)
    {
        if (config[key] is not JsonArray)
        {
            config[key] = new JsonArray();
        }
    }

    private static void SetString(JsonObject obj, string key, string value) => obj[key] = value;

    private static void SetBool(JsonObject obj, string key, bool value) => obj[key] = value;

    private static void SetNumber(JsonObject obj, string key, int value) => obj[key] = value;

    private static void CopyNode(JsonObject source, Dictionary<string, object?> target, string sourceKey, string targetKey)
    {
        if (source[sourceKey] is not JsonNode node)
        {
            return;
        }

        target[targetKey] = node.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when node.AsValue().TryGetValue<int>(out var integer) => integer,
            JsonValueKind.Number when node.AsValue().TryGetValue<long>(out var number) => number,
            JsonValueKind.Number when node.AsValue().TryGetValue<double>(out var number) => number,
            JsonValueKind.String => node.GetValue<string>(),
            _ => node.ToJsonString()
        };
    }

    private static void CopyString(JsonObject source, Dictionary<string, object?> target, string sourceKey, string targetKey)
    {
        if (source[sourceKey] is JsonValue value && value.TryGetValue<string>(out var parsed))
        {
            target[targetKey] = parsed;
        }
    }

    private static void CopyBool(JsonObject source, Dictionary<string, object?> target, string sourceKey, string targetKey)
    {
        if (source[sourceKey] is JsonValue value && value.TryGetValue<bool>(out var parsed))
        {
            target[targetKey] = parsed;
        }
    }

    private static void CopyNumber(JsonObject source, Dictionary<string, object?> target, string sourceKey, string targetKey)
    {
        if (source[sourceKey] is JsonValue value && value.TryGetValue<int>(out var integer))
        {
            target[targetKey] = integer;
        }
    }

    private static string GetNodeString(JsonObject obj, string key, string fallback)
    {
        return obj[key] is JsonValue value && value.TryGetValue<string>(out var parsed) ? parsed : fallback;
    }

    private static int GetNodeInt(JsonObject obj, string key, int fallback)
    {
        return obj[key] is JsonValue value && value.TryGetValue<int>(out var parsed) ? parsed : fallback;
    }

    private static string? ResolveImportInstallPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return null;
        }

        var candidates = new[]
        {
            path,
            Path.Combine(path, "serverfiles")
        };

        return candidates.FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, ExecutableName)));
    }

    private static string GetConfigPath(ServerInstance instance) => GetConfigPath(instance.InstallPath);

    private static string GetConfigPath(string installPath) => Path.Combine(installPath, ConfigFileName);

    private static string SanitizeAdditionalArguments(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var managedPrefixes = new[]
        {
            "-ip",
            "-queryPort",
            "-slotCount",
            "-name",
            "-log"
        };

        var remaining = new List<string>();
        var tokens = SplitCommandLine(value);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var managedPrefix = managedPrefixes.FirstOrDefault(prefix =>
                token.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                token.StartsWith(prefix + "=", StringComparison.OrdinalIgnoreCase));
            if (managedPrefix != null)
            {
                if (!token.Contains('=') &&
                    i + 1 < tokens.Count &&
                    !tokens[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    i++;
                }

                continue;
            }

            remaining.Add(token);
        }

        return string.Join(' ', remaining);
    }

    private static IReadOnlyList<string> SplitCommandLine(string value)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in value)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                AddToken(tokens, current);
                continue;
            }

            current.Append(c);
        }

        AddToken(tokens, current);
        return tokens;
    }

    private static void AddToken(List<string> tokens, StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        tokens.Add(current.ToString());
        current.Clear();
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string GenerateRolePassword(string prefix)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        Span<byte> bytes = stackalloc byte[GeneratedPasswordSuffixLength];
        RandomNumberGenerator.Fill(bytes);
        var chars = bytes.ToArray().Select(value => alphabet[value % alphabet.Length]).ToArray();
        return prefix + new string(chars);
    }

    private static bool LooksLikeLegacyGeneratedPassword(string roleName, string password)
    {
        if (password.Length <= MaximumRolePasswordLength ||
            !password.StartsWith(roleName, StringComparison.Ordinal) ||
            password.Length != roleName.Length + 10)
        {
            return false;
        }

        return password[roleName.Length..].All(char.IsLetterOrDigit);
    }

    private static bool GetBool(IReadOnlyDictionary<string, object?> settings, string key, bool fallback)
    {
        if (!settings.TryGetValue(key, out var value) || value == null)
        {
            return fallback;
        }

        return ToBool(value, fallback);
    }

    private static int GetInt(IReadOnlyDictionary<string, object?> settings, string key, int fallback)
    {
        if (!settings.TryGetValue(key, out var value) || value == null)
        {
            return fallback;
        }

        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static bool ToBool(object value, bool fallback)
    {
        return value is bool boolean
            ? boolean
            : bool.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
    }

    private static long ToLong(object value, long fallback)
    {
        return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static double ToDouble(object value, double fallback)
    {
        return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private sealed record RoleSettings(string Name, string Password, int ReservedSlots);

    private sealed record RoleObject(string Name, string Password, int ReservedSlots, JsonObject Json);

    private sealed record GameSettingField(string JsonKey, string SettingKey, GameSettingKind Kind, object DefaultValue);

    private enum GameSettingKind
    {
        String,
        Boolean,
        Integer,
        Number
    }

    private static readonly GameSettingField[] GameSettingFields =
    [
        new("playerHealthFactor", "game.playerHealthFactor", GameSettingKind.Number, 1.0),
        new("playerManaFactor", "game.playerManaFactor", GameSettingKind.Number, 1.0),
        new("playerStaminaFactor", "game.playerStaminaFactor", GameSettingKind.Number, 1.0),
        new("playerBodyHeatFactor", "game.playerBodyHeatFactor", GameSettingKind.Number, 1.0),
        new("playerDivingTimeFactor", "game.playerDivingTimeFactor", GameSettingKind.Number, 1.0),
        new("enableDurability", "game.enableDurability", GameSettingKind.Boolean, true),
        new("enableStarvingDebuff", "game.enableStarvingDebuff", GameSettingKind.Boolean, false),
        new("foodBuffDurationFactor", "game.foodBuffDurationFactor", GameSettingKind.Number, 1.0),
        new("fromHungerToStarving", "game.fromHungerToStarving", GameSettingKind.Integer, 600000000000L),
        new("shroudTimeFactor", "game.shroudTimeFactor", GameSettingKind.Number, 1.0),
        new("tombstoneMode", "game.tombstoneMode", GameSettingKind.String, "AddBackpackMaterials"),
        new("enableGliderTurbulences", "game.enableGliderTurbulences", GameSettingKind.Boolean, true),
        new("weatherFrequency", "game.weatherFrequency", GameSettingKind.String, "Normal"),
        new("fishingDifficulty", "game.fishingDifficulty", GameSettingKind.String, "Normal"),
        new("miningDamageFactor", "game.miningDamageFactor", GameSettingKind.Number, 1.0),
        new("plantGrowthSpeedFactor", "game.plantGrowthSpeedFactor", GameSettingKind.Number, 1.0),
        new("resourceDropStackAmountFactor", "game.resourceDropStackAmountFactor", GameSettingKind.Number, 1.0),
        new("factoryProductionSpeedFactor", "game.factoryProductionSpeedFactor", GameSettingKind.Number, 1.0),
        new("perkUpgradeRecyclingFactor", "game.perkUpgradeRecyclingFactor", GameSettingKind.Number, 0.5),
        new("perkCostFactor", "game.perkCostFactor", GameSettingKind.Number, 1.0),
        new("experienceCombatFactor", "game.experienceCombatFactor", GameSettingKind.Number, 1.0),
        new("experienceMiningFactor", "game.experienceMiningFactor", GameSettingKind.Number, 1.0),
        new("experienceExplorationQuestsFactor", "game.experienceExplorationQuestsFactor", GameSettingKind.Number, 1.0),
        new("randomSpawnerAmount", "game.randomSpawnerAmount", GameSettingKind.String, "Normal"),
        new("aggroPoolAmount", "game.aggroPoolAmount", GameSettingKind.String, "Normal"),
        new("enemyDamageFactor", "game.enemyDamageFactor", GameSettingKind.Number, 1.0),
        new("enemyHealthFactor", "game.enemyHealthFactor", GameSettingKind.Number, 1.0),
        new("enemyStaminaFactor", "game.enemyStaminaFactor", GameSettingKind.Number, 1.0),
        new("enemyPerceptionRangeFactor", "game.enemyPerceptionRangeFactor", GameSettingKind.Number, 1.0),
        new("bossDamageFactor", "game.bossDamageFactor", GameSettingKind.Number, 1.0),
        new("bossHealthFactor", "game.bossHealthFactor", GameSettingKind.Number, 1.0),
        new("threatBonus", "game.threatBonus", GameSettingKind.Number, 1.0),
        new("pacifyAllEnemies", "game.pacifyAllEnemies", GameSettingKind.Boolean, false),
        new("tamingStartleRepercussion", "game.tamingStartleRepercussion", GameSettingKind.String, "LoseSomeProgress"),
        new("dayTimeDuration", "game.dayTimeDuration", GameSettingKind.Integer, 1800000000000L),
        new("nightTimeDuration", "game.nightTimeDuration", GameSettingKind.Integer, 720000000000L),
        new("curseModifier", "game.curseModifier", GameSettingKind.String, "Normal")
    ];
}
