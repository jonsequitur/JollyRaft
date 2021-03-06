﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;

namespace JollyRaft
{
    public class Node : IDisposable
    {
        private readonly CompositeDisposable disposables = new CompositeDisposable();
        private readonly object grantVoteLocker = new object();
        private readonly NodeSettings nodeSettings;
        private int CommitIndex; // starts as 0
        private TimeSpan electionTimeout;
        private DateTimeOffset lastHeartBeat;
        private IEnumerable<Peer> Peers = new Peer[0];
        private bool started;

        public Node(NodeSettings nodeSettings)
        {
            if (nodeSettings == null)
            {
                throw new ArgumentNullException("nodeSettings");
            }

            this.nodeSettings = nodeSettings;
            State = State.Follower;
            electionTimeout = nodeSettings.ElectionTimeout;
            Id = this.nodeSettings.NodeId;
            LocalLog = new Log();
            ServerLog = new Log();
            Term = 1;

            disposables.Add(nodeSettings.PeerObserver
                                        .Subscribe(Observer.Create<IEnumerable<Peer>>(peers =>
                                                                                      {
                                                                                          Peers = peers.Where(p => p.Id != Id);
                                                                                          Peers.ForEach(p => Debug.WriteLine("{0} new peer discovered: {1}", NodeInfo(), p.Id));
                                                                                      },
                                                                                      ex => Debug.WriteLine("OnError: {0}", ex.Message),
                                                                                      () => Debug.WriteLine("OnCompleted"))));
        }

        public Log LocalLog { get; private set; }
        public Log ServerLog { get; private set; }
        public int Term { get; private set; }
        public string Id { get; private set; }
        public State State { get; private set; }
        public string CurrentLeader { get; private set; }

        public void Dispose()
        {
            disposables.Dispose();
        }

        public void Start()
        {
            if (started)
            {
                return;
            }

            //Election Holder
            electionTimeout = nodeSettings.ElectionTimeout.Randomize(10);
            disposables.Add(Observable.Interval(electionTimeout, nodeSettings.Scheduler)
                                      .Subscribe(async o => { await StartElection(); },
                                                 e => Debug.WriteLine(e.Message)));

            //Heartbeat pumper
            disposables.Add(Observable.Interval(nodeSettings.HeartBeatTimeout, nodeSettings.Scheduler)
                                      .Subscribe(async o => { await SendHeartBeat(); },
                                                 e => Debug.WriteLine(e.Message)));

            started = true;
        }

        public virtual async Task StartElection()
        {
            if (!Peers.Any())
            {
                return;
            }

            if (lastHeartBeat < nodeSettings.Scheduler.Now - electionTimeout)
            {
                if (State != State.Leader)
                {
                    State = State.Candidate;
                    Debug.WriteLine("{0}: Starting a new Election. current term {1}", NodeInfo(), Term);
                    Term++;

                    CurrentLeader = Id;
                    var successCount = 0;

                    Peers.ForEach(async p =>
                                        {
                                            var vote = await p.RequestVote(new VoteRequest(Id, Term, LocalLog.LastTerm, LocalLog.LastIndex));
                                            if (vote.VoteGranted)
                                            {
                                                Debug.WriteLine("{0}: Vote granted from {1}", NodeInfo(), p.Id);
                                                Interlocked.Increment(ref successCount);
                                            }
                                            else
                                            {
                                                if (vote.CurrentTerm > Term)
                                                {
                                                    StepDown(vote.CurrentTerm, p.Id);
                                                }
                                            }
                                        });

                    if (ConcencusIsReached(successCount) && State == State.Candidate)
                    {
                        State = State.Leader;
                        Debug.WriteLine("{0}: Elected as Leader. votes {1}. needed {2}", NodeInfo(), successCount, PeerAgreementsNeededForConcensus());
                        await SendHeartBeat();
                    }
                }
            }
        }

        private string NodeInfo()
        {
            return string.Format("{0}.{1}", Id, State);
        }

        private void HeartBeat(string id, int termNumber)
        {
            if (termNumber == Term)
            {
                if (State == State.Candidate)
                {
                    State = State.Follower;
                }
                Debug.WriteLine(string.Format("{0}: Beating Heart", NodeInfo()));
                lastHeartBeat = nodeSettings.Scheduler.Now;
                CurrentLeader = id;
            }
            else
            {
                //Tried to heartbeat but failed. current term 2 voted for node3 . sender term 2 sender Id node2
                Debug.WriteLine("{0}: Tried to heartbeat but failed. current term {1} voted for {2} . sender term {3} sender Id {4}",
                                Id,
                                Term,
                                CurrentLeader,
                                termNumber,
                                id);
            }
        }

        private bool ConcencusIsReached(int amountOfAgreements)
        {
            return amountOfAgreements >= PeerAgreementsNeededForConcensus();
        }

        private int PeerAgreementsNeededForConcensus()
        {
            return (Peers.Count() + 1)/2;
        }

        public virtual async Task<VoteResult> Vote(VoteRequest request)
        {
            if (request.Term < Term)
            {
                return new VoteResult(Term, false);
            }
            //this needs some sort of lock around it. although it would be very rare in a deployed system, the nodes in unit tests have hit
            //this block at the exact same time, resulting in both nodes getting the vote.
            //using a lock for now. should be replaced at some point with interlocked for perf
            lock (grantVoteLocker)
            {
                if (CurrentLeader == null || CurrentLeader == request.Id)
                {
                    Debug.WriteLine("{0}: Voting for {1} for term {2}", NodeInfo(), request.Id, request.Term);
                    State = State.Follower;
                    CurrentLeader = request.Id;
                    Term = request.Term;
                    lastHeartBeat = nodeSettings.Scheduler.Now;
                    return new VoteResult(Term, true);
                }

                return new VoteResult(Term, false);
            }
        }

        public async Task<LogResult> AddLog(string log)
        {
            if (State != State.Leader)
            {
                return new LogRejected
                       {
                           LeaderId = CurrentLeader
                       };
            }

            Debug.WriteLine("{0}: Adding Log {1}", NodeInfo(), log);
            return await SendAppendEntries(log, false);
        }

        public async Task SendHeartBeat()
        {
            if (State == State.Leader)
            {
                Debug.WriteLine(string.Format("{0}: Sending HearthBeat", NodeInfo()));
                await SendAppendEntries(null, true);
            }
        }

        private async Task CommitToServerLog(string log)
        {
            if (log == null)
            {
                return;
            }
            Debug.WriteLine("{0}: Commiting to server log {1}", NodeInfo(), log);
            ServerLog.Add(Term, log);
            CommitIndex = ServerLog.LastIndex;
            await SendHeartBeat(); 
        }

        private async Task<AppendEntriesResult> AppendAllTheEntries(bool heartBeat, Peer peer)
        {
            Debug.WriteLine("{0}: Appending all the entires! {1}", NodeInfo(), peer.Id);
            var result = await peer.AppendEntries(new AppendEntriesRequest(Id,
                                                                           Term,
                                                                           LocalLog.PreviousLogIndex,
                                                                           LocalLog.PreviousLogTerm,
                                                                           heartBeat
                                                                               ? LocalLog.Entries.Where(e => e.Index > peer.CommitIndex).ToArray()
                                                                               : LocalLog.Entries.ToArray(),
                                                                           CommitIndex));
            Debug.WriteLine("{0}: Adding AppendEntry result from {1} = {2}", NodeInfo(), peer.Id, result.Success);
            if (result.Success)
            {
                peer.CommitIndex = CommitIndex;
            }
            return result;
        }

        private async Task<LogResult> SendAppendEntries(string log, bool heartBeat)
        {
            if (!heartBeat)
            {
                LocalLog.Add(Term, log);
            }
            var results = new List<AppendEntriesResult>();

            Peers.ForEach(async peer =>
                                {
                                    Debug.WriteLine("{0}: sending append entries to {1}", NodeInfo(), peer.Id);
                                    var result = (await AppendAllTheEntries(heartBeat, peer)
                                                            .ToObservable()
                                                            .Timeout(TimeSpan.FromSeconds(5))
                                                            .Catch<AppendEntriesResult, TimeoutException>(e => Observable.Return(new AppendEntriesResult(0, false))));

                                    if (result.CurrentTerm > Term)
                                    {
                                        StepDown(result.CurrentTerm, peer.Id);
                                    }

                                    results.Add(result);
                                    Debug.WriteLine("{0}: Got a result {1}", NodeInfo(), peer.Id);
                                });

            if (results.Count(r => r.Success) >= PeerAgreementsNeededForConcensus())
            {
                await CommitToServerLog(log);
            }

            return new LogCommited
                   {
                       ServerLog = ServerLog
                   };
        }

        private Task<Log> SendAppendEntriesInParallel(string log, bool heartBeat)
        {
            if (!heartBeat)
            {
                LocalLog.Add(Term, log);
            }

            var tcs = new TaskCompletionSource<Log>();
            var successCount = 0;

            Peers.ForEach(peer => Task.Run(async () =>
                                                 {
                                                     AppendEntriesResult result;
                                                     Debug.WriteLine("{0}: sending append entries to {1}", NodeInfo(), peer.Id);
                                                     result = await AppendAllTheEntries(heartBeat, peer);
                                                     Debug.WriteLine("{0}: Got a result {1}", NodeInfo(), peer.Id);
                                                     return result;
                                                 })
                                      .ContinueWith(async t =>
                                                          {
                                                              Debug.WriteLine("{0}: ContinueingWith {1}", NodeInfo(), t.Result.Success);
                                                              if (t.Result.Success)
                                                              {
                                                                  Interlocked.Increment(ref successCount);
                                                              }

                                                              if (successCount >= PeerAgreementsNeededForConcensus())
                                                              {
                                                                  await CommitToServerLog(log);
                                                                  tcs.SetResult(ServerLog);
                                                              }
                                                          }));

            return tcs.Task;
        }

        public virtual async Task<AppendEntriesResult> AppendEntries(AppendEntriesRequest request)
        {
            Debug.WriteLine("{0}: GOT APPEND ENTRIES CALL {1}: leader commit {2} : entries: {3}", NodeInfo(), request.Id, request.CommitIndex, String.Concat(request.Entries.Select(e => e.Log + " ")));
            HeartBeat(request.Id, request.Term);

            if (request.Entries == null || !request.Entries.Any()) //heartbeat
            {
                if (request.CommitIndex > ServerLog.LastIndex)
                {
                    Debug.WriteLine("{0}: HeartBeat Adding To Server Log up to index {1} leaderCommitIndex {2}", NodeInfo(), request.PreviousLogIndex, request.CommitIndex);
                    LocalLog.Entries.Where(e => e.Index > ServerLog.LastIndex && e.Index <= request.CommitIndex).ForEach(e => ServerLog.Add(e.Term, e.Log));
                }

                return new AppendEntriesResult(Term, true);
            }

            if (Term < request.Term) //servers term is behind. we need to ensure we are in follower state and reset who we have voted for
            {
                StepDown(request.Term, request.Id);
                return new AppendEntriesResult(Term, false);
            }

            if (request.Term < Term)
            {
                return new AppendEntriesResult(Term, false);
            }

            if (request.PreviousLogIndex == 0 || (request.PreviousLogIndex <= ServerLog.LastIndex &&
                                                  ServerLog.LastTerm == request.PreviousLogTerm))
            {
                Debug.WriteLine("{0}: huh");
            }

            //reply false if log doesnt contain an entry at prevLogIndex whose term matches prevlogTerm
            if (!LocalLog.Entries.Any(e => e.Index == request.PreviousLogIndex && e.Term == request.PreviousLogTerm)) //not to sure about this line yet
            {
                return new AppendEntriesResult(Term, false);
            }

            if (LocalLog.Entries.Any(e => e.Index == request.PreviousLogIndex && e.Term != request.PreviousLogTerm))
            {
                //delete the entry and all that follow it
                LocalLog.RemoveEntryAndThoseAfterIt(request.PreviousLogIndex);
            }

            if (request.Entries.Max(e => e.Index) > LocalLog.LastIndex)
            {
                Debug.WriteLine("{0}: Adding To Local Log up to index {1} leaderCommitIndex {2}", NodeInfo(), request.Entries.Max(e => e.Index), request.CommitIndex);
                request.Entries.Where(e => e.Index > LocalLog.LastIndex).ForEach(e => LocalLog.Add(e.Term, e.Log));
            }
            if (request.CommitIndex > ServerLog.LastIndex)
            {
                Debug.WriteLine("{0}: Adding To Server Log up to index {1} leaderCommitIndex {2}", NodeInfo(), request.PreviousLogIndex, request.CommitIndex);
                request.Entries.Where(e => e.Index > ServerLog.LastIndex && e.Index <= request.CommitIndex).ForEach(e => ServerLog.Add(e.Term, e.Log));
            }

            if (request.CommitIndex > CommitIndex)
            {
                CommitIndex = Math.Min(request.CommitIndex, LocalLog.LastIndex);
            }

            return new AppendEntriesResult(Term, true);
        }

        private void StepDown(int newTerm, string idOfNodeWithHigherTerm)
        {
            Debug.WriteLine("{0}: Stepping down due to Node {1} having a higher term. new term: {2} old term: {3} ",
                            Id,
                            idOfNodeWithHigherTerm,
                            newTerm,
                            Term);
            State = State.Follower;
            Term = newTerm;
            CurrentLeader = null;
        }

        public IEnumerable<string> GetPeerIds()
        {
            return Peers.Select(p => p.Id);
        }

        public Peer AsPeer()
        {
            return new Peer(Id, Vote, AppendEntries);
        }
    }
}