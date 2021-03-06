﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Disposables;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using FluentAssertions;
using JollyRaft;
using Microsoft.Owin.Testing;
using Newtonsoft.Json;
using NUnit.Framework;
using SampleWebApplication;

namespace Samples
{
    [TestFixture]
    public class ServerTests
    {
        [SetUp]
        public void SetUp()
        {
            var peerObservable = new Subject<IEnumerable<Peer>>();
            CreateServer(peerObservable, "node1");
            CreateServer(peerObservable, "node2");
            CreateServer(peerObservable, "node3");

            var peers = clientsById.Select(c =>
                                           new Peer(c.Key,
                                                    async request => c.Value.PostAsync("requestvote/",
                                                                                       request.AsStringContent())
                                                                      .Result
                                                                      .Content
                                                                      .ReadAsStringAsync().Result
                                                                      .DeserializeAs<VoteResult>(),
                                                    async request => c.Value.PostAsync("appendentries/",
                                                                                       request.AsStringContent())
                                                                      .Result
                                                                      .Content
                                                                      .ReadAsStringAsync().Result
                                                                      .DeserializeAs<AppendEntriesResult>())
                );

            peerObservable.OnNext(peers);
        }

        private readonly List<HttpClient> httpClients = new List<HttpClient>();
        private readonly CompositeDisposable disposables = new CompositeDisposable();
        private readonly List<KeyValuePair<string, HttpClient>> clientsById = new List<KeyValuePair<string, HttpClient>>();

        private void CreateServer(Subject<IEnumerable<Peer>> peerObservable, string nodeId)
        {
            var server = TestServer.Create(app =>
                                           {
                                               var startup = new StartUp();
                                               startup.ConfigureApplication(app);
                                               startup.ConfigureAndStartRaft(nodeId, peerObservable);
                                           });
            Trace.Listeners.RemoveAt(Trace.Listeners.Count - 1);
            disposables.Add(server);
            httpClients.Add(server.HttpClient);

            clientsById.Add(new KeyValuePair<string, HttpClient>(nodeId, server.HttpClient));
        }

        [Test]
        public async Task a_leader_is_elected_amongst_the_servers()
        {
            await Task.Delay(TimeSpan.FromSeconds(5));

            httpClients.Should().ContainSingle(c => c.GetAsync("state").Result.Content.ReadAsStringAsync().Result.DeserializeAs<State>() == State.Leader);
        }

        [Test]
        public void each_server_has_a_distinct_node_id()
        {
            var id1 = JsonConvert.DeserializeObject<string>(httpClients.First().GetAsync("name").Result.Content.ReadAsStringAsync().Result);
            var id2 = JsonConvert.DeserializeObject<string>(httpClients.Skip(1).First().GetAsync("name").Result.Content.ReadAsStringAsync().Result);

            id1.Should().NotBe(id2);
        }

        [Test]
        public void serverIsReachable()
        {
            httpClients.First().GetAsync("name").Result.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void when_log_is_requestedof_a_follower_bad_request_is_returned()
        {
            httpClients.First().PostAsync("log",
                                          new
                                          {
                                              Log = "first commit"
                                          }.AsStringContent())
                       .Result.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }
}