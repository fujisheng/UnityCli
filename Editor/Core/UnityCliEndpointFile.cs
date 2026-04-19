using System;
using System.Globalization;
using System.IO;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UnityCli.Editor.Core
{
    public static class UnityCliEndpointFile
    {
        const string SessionInstanceIdKey = "UnityCli.InstanceId";
        const string SessionGenerationKey = "UnityCli.Generation";

        public const string ProtocolVersion = "1.0";

        public static string DirectoryPath => Path.Combine(GetProjectRoot(), "Library", "UnityCliBridge");

        public static string FilePath => Path.Combine(DirectoryPath, "endpoint.json");

        public static BridgeEndpoint CreateEndpoint(string pipeName, string token)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
            {
                throw new ArgumentException("PipeName 不能为空。", nameof(pipeName));
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new ArgumentException("Token 不能为空。", nameof(token));
            }

            CleanupResidualFile();
            var instanceId = GetOrCreateInstanceId();
            var generation = GetNextGeneration(instanceId);
            return new BridgeEndpoint
            {
                protocolVersion = ProtocolVersion,
                transport = BridgeEndpoint.TransportNamedPipe,
                pipeName = pipeName,
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                instanceId = instanceId,
                generation = generation,
                token = token,
                startedAt = DateTime.UtcNow
            };
        }

        public static void Write(BridgeEndpoint endpoint)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, UnityCliJson.Serialize(endpoint));
        }

        public static bool TryRead(out BridgeEndpoint endpoint)
        {
            endpoint = null;
            if (!File.Exists(FilePath))
            {
                return false;
            }

            try
            {
                var json = File.ReadAllText(FilePath);
                if (!UnityCliJson.TryDeserializeObject(json, out var root, out _))
                {
                    return false;
                }

                endpoint = new BridgeEndpoint
                {
                    protocolVersion = ReadString(root, "protocolVersion"),
                    transport = ReadString(root, "transport"),
                    pipeName = ReadString(root, "pipeName"),
                    pid = ReadInt(root, "pid"),
                    instanceId = ReadString(root, "instanceId"),
                    generation = ReadLong(root, "generation"),
                    token = ReadString(root, "token"),
                    startedAt = ReadDateTime(root, "startedAt")
                };
                return true;
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning($"[UnityCli] 读取 endpoint.json 失败：{FilePath}\n{exception}");
                endpoint = null;
                return false;
            }
        }

        public static void Delete(BridgeEndpoint expectedEndpoint = null)
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            if (expectedEndpoint != null
                && TryRead(out var currentEndpoint)
                && currentEndpoint != null
                && currentEndpoint.pid != expectedEndpoint.pid)
            {
                return;
            }

            try
            {
                File.Delete(FilePath);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning($"[UnityCli] 删除 endpoint.json 失败：{FilePath}\n{exception}");
            }
        }

        public static void CleanupResidualFile()
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            if (!TryRead(out var endpoint) || endpoint == null)
            {
                Delete();
                return;
            }

            if (endpoint.pid <= 0)
            {
                Delete();
                return;
            }

            if (IsProcessAlive(endpoint.pid))
            {
                return;
            }

            Delete(endpoint);
        }

        public static void ResetSessionState()
        {
            SessionState.EraseString(SessionInstanceIdKey);
            SessionState.EraseString(SessionGenerationKey);
        }

        static string GetProjectRoot()
        {
            return Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();
        }

        static string GetOrCreateInstanceId()
        {
            var instanceId = SessionState.GetString(SessionInstanceIdKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(instanceId))
            {
                return instanceId;
            }

            instanceId = Guid.NewGuid().ToString("N");
            SessionState.SetString(SessionInstanceIdKey, instanceId);
            return instanceId;
        }

        static long GetNextGeneration(string instanceId)
        {
            if (long.TryParse(SessionState.GetString(SessionGenerationKey, string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sessionGeneration)
                && sessionGeneration >= 0)
            {
                var nextSessionGeneration = sessionGeneration + 1;
                SessionState.SetString(SessionGenerationKey, nextSessionGeneration.ToString(CultureInfo.InvariantCulture));
                return nextSessionGeneration;
            }

            var nextGeneration = 1L;
            if (TryRead(out var existingEndpoint)
                && existingEndpoint != null
                && existingEndpoint.pid == System.Diagnostics.Process.GetCurrentProcess().Id
                && string.Equals(existingEndpoint.instanceId, instanceId, StringComparison.Ordinal))
            {
                nextGeneration = Math.Max(1L, existingEndpoint.generation + 1L);
            }

            SessionState.SetString(SessionGenerationKey, nextGeneration.ToString(CultureInfo.InvariantCulture));
            return nextGeneration;
        }

        static bool IsProcessAlive(int pid)
        {
            try
            {
                using (var process = System.Diagnostics.Process.GetProcessById(pid))
                {
                    return !process.HasExited;
                }
            }
            catch
            {
                return false;
            }
        }

        static string ReadString(System.Collections.Generic.IDictionary<string, object> root, string key)
        {
            if (!root.TryGetValue(key, out var value) || value == null)
            {
                return string.Empty;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        static int ReadInt(System.Collections.Generic.IDictionary<string, object> root, string key)
        {
            if (!root.TryGetValue(key, out var value) || value == null)
            {
                return 0;
            }

            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        static long ReadLong(System.Collections.Generic.IDictionary<string, object> root, string key)
        {
            if (!root.TryGetValue(key, out var value) || value == null)
            {
                return 0L;
            }

            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        static DateTime ReadDateTime(System.Collections.Generic.IDictionary<string, object> root, string key)
        {
            if (!root.TryGetValue(key, out var value) || value == null)
            {
                return DateTime.UtcNow;
            }

            if (value is DateTime dateTime)
            {
                return dateTime;
            }

            var raw = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                return parsed;
            }

            return DateTime.UtcNow;
        }
    }
}
