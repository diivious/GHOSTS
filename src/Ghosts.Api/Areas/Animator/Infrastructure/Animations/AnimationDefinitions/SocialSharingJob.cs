// Copyright 2017 Carnegie Mellon University. All Rights Reserved. See LICENSE.md file for terms.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Ghosts.Animator.Extensions;
using ghosts.api.Areas.Animator.Hubs;
using ghosts.api.Areas.Animator.Infrastructure.ContentServices;
using Ghosts.Api.Infrastructure;
using Ghosts.Api.Infrastructure.Data;
using ghosts.api.Infrastructure.Models;
using ghosts.api.Infrastructure.Services;
using Ghosts.Domain;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using NLog;
using RestSharp;

namespace ghosts.api.Areas.Animator.Infrastructure.Animations.AnimationDefinitions;

public class SocialSharingJob
{
    private static readonly Logger _log = LogManager.GetCurrentClassLogger();
    private readonly ApplicationSettings _configuration;
    private readonly Random _random;
    private const string SavePath = "_output/socialsharing/";
    private readonly int _currentStep;
    private readonly IHubContext<ActivityHub> _activityHubContext;
    private readonly CancellationToken _cancellationToken;
    private readonly ApplicationDbContext _context;
    private readonly IMachineUpdateService _updateService;

    public SocialSharingJob(ApplicationSettings configuration, ApplicationDbContext context, Random random,
        IHubContext<ActivityHub> activityHubContext, IMachineUpdateService updateService, CancellationToken cancellationToken)
    {
        try
        {
            this._activityHubContext = activityHubContext;
            this._configuration = configuration;
            this._random = random;
            this._context = context;
            this._cancellationToken = cancellationToken;
            this._updateService = updateService;

            if (!_configuration.AnimatorSettings.Animations.SocialSharing.IsInteracting)
            {
                _log.Trace($"Social sharing is not interacting. Exiting...");
                return;
            }

            if (!Directory.Exists(SavePath))
            {
                Directory.CreateDirectory(SavePath);
            }

            while (!this._cancellationToken.IsCancellationRequested)
            {
                if (this._currentStep > _configuration.AnimatorSettings.Animations.SocialSharing.MaximumSteps)
                {
                    _log.Trace($"Maximum steps met: {this._currentStep - 1}. Social sharing is exiting...");
                    return;
                }

                this.Step();
                Thread.Sleep(this._configuration.AnimatorSettings.Animations.SocialSharing.TurnLength);
                this._currentStep++;
            }
        }
        catch (ThreadInterruptedException e)
        {
            _log.Info("Social sharing thread interrupted!");
            _log.Error(e);
        }
        catch (Exception e)
        {
            _log.Error(e);
        }
        _log.Info("Social sharing job complete. Exiting...");
    }

    private async void Step()
    {
        _log.Trace("Social sharing step proceeding...");
        
        var contentService =
            new ContentCreationService(_configuration.AnimatorSettings.Animations.SocialSharing.ContentEngine);

        //take some random NPCs
        var lines = new StringBuilder();
        var rawAgents = this._context.Npcs.ToList();
        if (!rawAgents.Any())
        {
            _log.Warn("No NPCs found. Is this correct?");
            return;
        }
        _log.Trace($"Found {rawAgents.Count()} raw agents...");

        var agents = rawAgents.Shuffle(_random).Take(_random.Next(5, 20)).ToList();
        _log.Trace($"Processing {agents.Count()} agents...");
        foreach (var agent in agents)
        {
            _log.Trace($"Processing agent {agent.NpcProfile.Email}...");
            var tweetText = await contentService.GenerateTweet(agent);
            if (string.IsNullOrEmpty(tweetText))
            {
                _log.Trace($"Content service generated no payload...");
                return;
            }

            lines.AppendFormat($"{DateTime.Now},{agent.Id},\"{tweetText}\"{Environment.NewLine}");

            // the payloads to socializer are a bit randomized
            var userFormValue = new[] { "user", "usr", "u", "uid", "user_id", "u_id" }.RandomFromStringArray();
            var messageFormValue =
                new[] { "message", "msg", "m", "message_id", "msg_id", "msg_text", "text", "payload" }
                    .RandomFromStringArray();

            if (_configuration.AnimatorSettings.Animations.SocialSharing.IsSendingTimelinesDirectToSocializer)
            {
                var client = new RestClient(_configuration.AnimatorSettings.Animations.SocialSharing.PostUrl);
                var request = new RestRequest("/", Method.Post)
                {
                    RequestFormat = DataFormat.Json
                };
                request.AddParameter(userFormValue, agent.NpcProfile.Name.ToString());
                request.AddParameter(messageFormValue, tweetText);

                try
                {
                    var response = client.Execute(request);
                    if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.NoContent)
                    {
                        throw (new Exception(
                            $"Socializer responded with {response.StatusCode} to the request agent: {agent.NpcProfile.Name} text: {tweetText}"));
                    }
                }
                catch (Exception e)
                {
                    _log.Error(
                        $"Could not post timeline command to Socializer {_configuration.AnimatorSettings.Animations.SocialSharing.PostUrl}: {e}");
                }
            }

            if (_configuration.AnimatorSettings.Animations.SocialSharing.IsSendingTimelinesToGhostsApi)
            {
                var payload = new 
                {
                    Uri = _configuration.AnimatorSettings.Animations.SocialSharing.PostUrl,
                    Category = "social",
                    Method = "POST",
                    Headers = new Dictionary<string, string>
                    {
                        { "u", agent.NpcProfile.Email }
                    },
                    FormValues = new Dictionary<string, string>
                    {
                        { userFormValue, agent.NpcProfile.Email },
                        { messageFormValue, tweetText }
                    }
                };

                var t = new Timeline();
                t.Id = Guid.NewGuid();
                t.Status = Timeline.TimelineStatus.Run;
                var th = new TimelineHandler();
                th.HandlerType = HandlerType.BrowserFirefox;
                th.Initial = "about:blank";
                th.UtcTimeOn = new TimeSpan(0, 0, 0);
                th.UtcTimeOff = new TimeSpan(23, 59, 59);
                th.HandlerArgs = new Dictionary<string, object>();
                th.HandlerArgs.Add("isheadless", "false");
                th.Loop = false;
                var te = new TimelineEvent();
                te.Command = "browse";
                te.CommandArgs = new List<object>();
                te.CommandArgs.Add(JsonConvert.SerializeObject(payload));
                te.DelayAfter = 0;
                te.DelayBefore = 0;
                th.TimeLineEvents.Add(te);
                t.TimeLineHandlers.Add(th);
                
                var machineUpdate = new MachineUpdate();
                if (agent.MachineId.HasValue)
                {
                    machineUpdate.MachineId = agent.MachineId.Value;
                }
                
                machineUpdate.Update = JsonConvert.SerializeObject(t);
                machineUpdate.Username = agent.NpcProfile.Email;
                machineUpdate.Status = StatusType.Active;
                machineUpdate.Type = UpdateClientConfig.UpdateType.TimelinePartial;

                _ = await _updateService.CreateAsync(machineUpdate, _cancellationToken);
            }

            //post to hub
            await this._activityHubContext.Clients.All.SendAsync("show",
                "1",
                agent.Id.ToString(),
                "social",
                tweetText,
                DateTime.Now.ToString(CultureInfo.InvariantCulture), 
                cancellationToken: _cancellationToken);
        }

        await File.AppendAllTextAsync($"{SavePath}tweets.csv", lines.ToString(), _cancellationToken);
    }
}