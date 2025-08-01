﻿// Copyright 2017 Carnegie Mellon University. All Rights Reserved. See LICENSE.md file for terms.

using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Ghosts.Client.Universal.Infrastructure;
using Ghosts.Domain;
using Ghosts.Domain.Code;
using NLog;

namespace Ghosts.Client.Universal.Comms;

/// <summary>
/// The client ID is used in the header to save having to send hostname/user/fqdn/etc. information with every request
/// </summary>
public class CheckId
{
    private static readonly Logger _log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// The actual path to the client id file, specified in application config
    /// </summary>
    private readonly string _idFile = ApplicationDetails.InstanceFiles.Id;

    private DateTime _lastChecked = DateTime.Now;
    private string _id = string.Empty;

    public CheckId()
    {
        _log.Info("CheckId instance created");
        _log.Debug($"CheckId instantiated with ID File Path: {_idFile}");
    }

    /// <summary>
    /// Gets the agent's current id from local instance, and if it does not exist, gets an id from the server and saves it locally
    /// </summary>
    public string Id
    {
        get
        {
            _log.Trace("Id property getter invoked");

            if (!string.IsNullOrEmpty(_id))
            {
                _log.Debug($"Returning cached Id: {_id}");
                return _id;
            }

            try
            {
                if (!File.Exists(_idFile))
                {
                    _log.Warn($"ID file not found at path: {_idFile}");

                    if (DateTime.Now > _lastChecked.AddMinutes(5))
                    {
                        _log.Error("Skipping check for ID from server due to recent check within 5 minutes.");
                        return string.Empty;
                    }

                    _log.Info("Attempting to retrieve ID from server.");
                    _lastChecked = DateTime.Now;
                    _id = Run().GetAwaiter().GetResult(); //todo: cringe :)

                    if (string.IsNullOrEmpty(_id))
                    {
                        _log.Warn("Retrieved ID is empty after attempting to fetch from server.");
                    }

                    return _id;
                }

                _id = File.ReadAllText(_idFile).Trim();
                _log.Info($"ID retrieved from local file: {_id}");
                return _id;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Exception occurred while retrieving ID.");
                return string.Empty;
            }
        }
        set
        {
            _log.Debug($"Setting ID to: {value}");
            _id = value;
        }
    }

    /// <summary>
    /// API call to get client ID (probably based on hostname, but configurable) and saves it locally
    /// </summary>
    /// <returns></returns>
    private async Task<string> Run()
    {
        _log.Trace("Run method started: Attempting to fetch ID from server.");

        var fetchedId = string.Empty;

        if (!Program.Configuration.Id.IsEnabled)
        {
            _log.Warn("ID retrieval is disabled in the configuration.");
            return fetchedId;
        }

        var machine = new ResultMachine();
        GuestInfoVars.Load(machine);

        try
        {
            // Call home
            using var client = HttpClientBuilder.Build(machine, false);
            try
            {
                _log.Info($"Attempting to connect to ID endpoint: {Program.ConfigurationUrls.Id}");

                var response = await client.GetAsync(Program.ConfigurationUrls.Id);
                if (!response.IsSuccessStatusCode)
                {
                    _log.Error($"Failed to fetch ID. Status code: {response.StatusCode}");
                    return fetchedId;
                }

                await using var responseStream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(responseStream);
                fetchedId = (await reader.ReadToEndAsync()).Trim();
                _log.Info($"ID successfully received from server: {fetchedId}");
            }
            catch (WebException wex)
            {
                _log.Warn(wex, "WebException occurred while attempting to fetch ID.");

                if (wex.Message.StartsWith("The remote name could not be resolved:"))
                {
                    _log.Warn($"API not reachable: {wex.Message}");
                }
                else if (wex.Response is HttpWebResponse { StatusCode: HttpStatusCode.NotFound })
                {
                    _log.Warn($"ID not found (404): {wex.Message}");
                }
                else
                {
                    _log.Error(wex, $"WebException encountered: {wex.Message}");
                }
            }
            catch (Exception e)
            {
                _log.Error(e, $"Unexpected exception during ID retrieval: {e.Message}");
            }
        }
        catch (Exception e)
        {
            _log.Error(e, $"Cannot connect to API: {e.Message}");
            return string.Empty;
        }

        // Remove potential surrounding quotes
        fetchedId = fetchedId.Replace("\"", "");

        if (!Directory.Exists(ApplicationDetails.InstanceFiles.Path))
        {
            try
            {
                Directory.CreateDirectory(ApplicationDetails.InstanceFiles.Path);
                _log.Info($"Created directory for ID file: {ApplicationDetails.InstanceFiles.Path}");
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"Failed to create directory: {ApplicationDetails.InstanceFiles.Path}");
                return string.Empty;
            }
        }

        if (string.IsNullOrEmpty(fetchedId))
        {
            _log.Warn("Fetched ID is empty after processing.");
            return string.Empty;
        }

        try
        {
            // Save returned ID
            await File.WriteAllTextAsync(_idFile, fetchedId);
            _log.Info($"ID successfully written to file: {_idFile}");
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"Failed to write ID to file: {_idFile}");
            return string.Empty;
        }

        return fetchedId;
    }

    /// <summary>
    /// Writes the provided ID to the ID file, ensuring proper formatting and directory existence.
    /// </summary>
    /// <param name="id">The ID to write.</param>
    public static void WriteId(string id)
    {
        _log.Trace("WriteId method invoked.");

        if (string.IsNullOrEmpty(id))
        {
            _log.Warn("Attempted to write an empty or null ID.");
            return;
        }

        try
        {
            _log.Debug($"Received ID for writing: {id}");
            id = id.Replace("\"", "").Trim();

            if (!Directory.Exists(ApplicationDetails.InstanceFiles.Path))
            {
                Directory.CreateDirectory(ApplicationDetails.InstanceFiles.Path);
                _log.Info($"Created directory for ID file: {ApplicationDetails.InstanceFiles.Path}");
            }

            // Save returned ID
            File.WriteAllText(ApplicationDetails.InstanceFiles.Id, id);
            _log.Info($"ID successfully written to file: {ApplicationDetails.InstanceFiles.Id}");
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"Failed to write ID to file: {ApplicationDetails.InstanceFiles.Id}");
        }
    }
}
