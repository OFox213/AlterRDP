using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using AlterApp.Services.Interfaces;
using AlterApp.Models;

namespace AlterApp.Services
{
    internal class AppSettingsService : IAppSettingsService
    {
        private static readonly JsonSerializerOptions _jsonSerializerOptions = new(JsonSerializerDefaults.General)
        {
            WriteIndented = false,
        };

        public AppSettingsService()
        {
        }

        public T GetSettingValue<T>(string name, T defaultValue)
        {
            return ReadAppSettingValue(name, defaultValue);
        }

        private static T ReadAppSettingValue<T>(string settingName, T defaultValue)
        {
            JsonObject jsonObject = ReadSettingsJsonObject();
            JsonNode? currentNode = GetJsonNode(jsonObject, settingName);
            if (currentNode == null)
            {
                return defaultValue;
            }

            try
            {
                return currentNode.Deserialize<T>(_jsonSerializerOptions) ?? defaultValue;
            }
            catch (JsonException)
            {
                return defaultValue;
            }
        }

        public int SetSettingValue<T>(string name, T newValue)
        {
            return WriteAppSettingValue(name, newValue);
        }

        private static int WriteAppSettingValue<T>(string settingName, T newValue)
        {
            JsonObject jsonObject = ReadSettingsJsonObject();
            SetJsonNodeValue(jsonObject, settingName, JsonSerializer.SerializeToNode(newValue, _jsonSerializerOptions));
            string jsonText = jsonObject.ToJsonString(_jsonSerializerOptions);

            string connectionString = GetSettingDbConnectionString();
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        $@"
                        UPDATE {AppConstants.AppSettingTableName}
                        SET {AppConstants.AppSettingJsonColumnName} = @json
                        WHERE ROWID = {AppConstants.AppSettingRowId};
                        ";
                    command.Parameters.AddWithValue("@json", jsonText);
                    return command.ExecuteNonQuery();
                }
            }
        }

        private static JsonObject ReadSettingsJsonObject()
        {
            string connectionString = GetSettingDbConnectionString();
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        $@"
                        SELECT
                            {AppConstants.AppSettingJsonColumnName}
                        FROM
                            {AppConstants.AppSettingTableName}
                        WHERE ROWID = {AppConstants.AppSettingRowId};
                        ";

                    object? result = command.ExecuteScalar();
                    if (result is string jsonText && JsonNode.Parse(jsonText) is JsonObject jsonObject)
                    {
                        return jsonObject;
                    }
                }
            }

            return new JsonObject();
        }

        private static JsonNode? GetJsonNode(JsonObject jsonObject, string settingName)
        {
            JsonNode? currentNode = jsonObject;
            foreach (string segment in settingName.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                currentNode = currentNode?[segment];
                if (currentNode == null)
                {
                    return null;
                }
            }

            return currentNode;
        }

        private static void SetJsonNodeValue(JsonObject jsonObject, string settingName, JsonNode? value)
        {
            string[] segments = settingName.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return;
            }

            JsonObject currentObject = jsonObject;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (currentObject[segments[i]] is not JsonObject nextObject)
                {
                    nextObject = new JsonObject();
                    currentObject[segments[i]] = nextObject;
                }

                currentObject = nextObject;
            }

            currentObject[segments[^1]] = value;
        }

        private static string GetSettingDbConnectionString()
        {
            string appSettingFilePath = GetSettingFilePath();
            return string.Format("Data Source={0}", appSettingFilePath);
        }

        private static string GetSettingFilePath()
        {
            string settingStoreFolderPath = GetSettingStoreFolderPath();
            string settingFilePath = Path.Combine(settingStoreFolderPath, AppConstants.SettingFileName);
            if (!File.Exists(settingFilePath))
            {
                InitializeSettingFile(settingFilePath);
            }
            return settingFilePath;
        }

        private static string GetSettingStoreFolderPath()
        {
            string? appVersion = AppConstants.GetAppVersionSemanticPart();
            string settingStoreFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppConstants.SettingStoreFolderName, appVersion ?? "unspecified");
            if (!Directory.Exists(settingStoreFolderPath))
            {
                Directory.CreateDirectory(settingStoreFolderPath);
            }
            return settingStoreFolderPath;
        }

        private static void InitializeSettingFile(string settingFilePath)
        {
            using Stream? templateStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(AppConstants.SettingFileTemplateResourceName);
            if (templateStream == null)
            {
                throw new FileNotFoundException("The embedded setting template resource was not found.", AppConstants.SettingFileTemplateResourceName);
            }

            using FileStream settingFileStream = File.Create(settingFilePath);
            templateStream.CopyTo(settingFileStream);
        }
    }
}
