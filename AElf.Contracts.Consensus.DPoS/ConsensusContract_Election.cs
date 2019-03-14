using System;
using System.Collections.Generic;
using System.Linq;
using AElf.Common;
using AElf.Consensus.DPoS;
using AElf.Kernel;
using Google.Protobuf.WellKnownTypes;

namespace AElf.Contracts.Consensus.DPoS
{
    // ReSharper disable UnusedMember.Global
    public partial class ConsensusContract
    {
        private ulong CurrentAge => State.AgeField.Value;

        public ActionResult AnnounceElection(string alias = "")
        {
            var publicKey = Context.RecoverPublicKey().ToHex();
            // A voter cannot join the election before all his voting record expired.
            var tickets = State.TicketsMap[publicKey.ToStringValue()];
            if (tickets != null)
            {
                foreach (var voteToTransaction in tickets.VoteToTransactions)
                {
                    var votingRecord = State.VotingRecordsMap[voteToTransaction];
                    if (votingRecord != null)
                    {
                        Assert(votingRecord.IsWithdrawn,
                            ContractErrorCode.GetErrorMessage(ContractErrorCode.InvalidOperation,
                                "Voter can't announce election."));
                    }
                }
            }

            var candidates = State.CandidatesField.Value;
            if (candidates != null)
            {
                Assert(!candidates.PublicKeys.Contains(publicKey),
                    ContractErrorCode.GetErrorMessage(ContractErrorCode.InvalidOperation,
                        "Already announced election."));
                candidates.AddCandidate(Context.RecoverPublicKey());
            }
            else
            {
                candidates = new Candidates
                {
                    PublicKeys = {publicKey},
                    Addresses = {Address.FromPublicKey(Context.RecoverPublicKey())}
                };
            }

            State.CandidatesField.Value = candidates;

            if (alias == "" || alias.Length > DPoSContractConsts.AliasLimit)
            {
                alias = publicKey.Substring(0, DPoSContractConsts.AliasLimit);
            }

            var publicKeyOfThisAlias = State.AliasesLookupMap[alias.ToStringValue()];
            if (publicKeyOfThisAlias != null &&
                publicKey == publicKeyOfThisAlias.Value)
            {
                return new ActionResult {Success = true};
            }

            State.AliasesLookupMap[alias.ToStringValue()] = publicKey.ToStringValue();
            State.AliasesMap[publicKey.ToStringValue()] = alias.ToStringValue();

            // Add this alias to history information of this candidate.
            var candidateHistoryInformation = State.HistoryMap[publicKey.ToStringValue()];
            if (candidateHistoryInformation != null)
            {
                if (!candidateHistoryInformation.Aliases.Contains(alias))
                {
                    candidateHistoryInformation.Aliases.Add(alias);
                    candidateHistoryInformation.CurrentAlias = alias;
                }

                State.HistoryMap[publicKey.ToStringValue()] = candidateHistoryInformation;
            }
            else
            {
                State.HistoryMap[publicKey.ToStringValue()] = new CandidateInHistory
                {
                    CurrentAlias = alias
                };
            }

            State.TokenContract.Lock(Context.Sender, DPoSContractConsts.LockTokenForElection);

            return new ActionResult {Success = true};
        }

        public ActionResult QuitElection()
        {
            var candidates = State.CandidatesField.Value;

            Assert(candidates != null,
                ContractErrorCode.GetErrorMessage(ContractErrorCode.InvalidField, nameof(State.CandidatesField)));

            var publicKey = Context.RecoverPublicKey().ToHex();

            Assert(candidates != null && candidates.PublicKeys.Contains(publicKey),
                ContractErrorCode.GetErrorMessage(ContractErrorCode.InvalidOperation, "Not announced."));

            Assert(candidates != null && candidates.RemoveCandidate(Context.RecoverPublicKey()),
                ContractErrorCode.GetErrorMessage(ContractErrorCode.AttemptFailed,
                    "Failed to remove this public key from candidates list."));

            State.CandidatesField.Value = candidates;

            State.TokenContract.Unlock(Context.Sender, DPoSContractConsts.LockTokenForElection);

            return new ActionResult {Success = true};
        }

        public string Vote(string candidatePublicKey, ulong amount, int lockTime)
        {
            Assert(lockTime.InRange(90, 1095),
                ContractErrorCode.GetErrorMessage(ContractErrorCode.InvalidOperation, "Lock days is illegal."));

            // Cannot vote to non-candidate.
            var candidates = State.CandidatesField.Value;
            Assert(candidates != null, ContractErrorCode.GetErrorMessage(ContractErrorCode.NotFound, "No candidate."));
            Assert(candidates != null && candidates.PublicKeys.Contains(candidatePublicKey),
                ContractErrorCode.GetErrorMessage(ContractErrorCode.InvalidOperation,
                    "Target didn't announce election."));

            var voterPublicKey = Context.RecoverPublicKey().ToHex();
            // A candidate cannot vote to anybody.
            Assert(candidates != null && !candidates.PublicKeys.Contains(voterPublicKey),
                ContractErrorCode.GetErrorMessage(ContractErrorCode.InvalidOperation, "Candidate can't vote."));

            // Transfer the tokens to Consensus Contract address.
            State.TokenContract.Lock(Context.Sender, amount);

            var currentTermNumber = State.CurrentTermNumberField.Value;
            var currentRoundNumber = State.CurrentRoundNumberField.Value;

            // To make up a VotingRecord instance.
            var blockchainStartTimestamp = State.BlockchainStartTimestamp.Value ?? DateTime.UtcNow.ToTimestamp();

            var votingRecord = new VotingRecord
            {
                Count = amount,
                From = voterPublicKey,
                To = candidatePublicKey,
                RoundNumber = currentRoundNumber,
                TransactionId = Context.TransactionId,
                VoteAge = CurrentAge,
                UnlockAge = CurrentAge + (ulong) lockTime,
                TermNumber = currentTermNumber,
                VoteTimestamp = blockchainStartTimestamp.ToDateTime().AddDays(CurrentAge).ToTimestamp(),
                UnlockTimestamp = blockchainStartTimestamp.ToDateTime().AddDays(CurrentAge + (ulong) lockTime)
                    .ToTimestamp()
            };

            votingRecord.LockDaysList.Add(lockTime);

            // Add the transaction id of this voting record to the tickets information of the voter.
            var tickets = State.TicketsMap[voterPublicKey.ToStringValue()];

            if (tickets != null)
            {
                tickets.VoteToTransactions.Add(votingRecord.TransactionId);
            }
            else
            {
                tickets = new Tickets();
                tickets.VoteToTransactions.Add(votingRecord.TransactionId);
            }

            tickets.VotedTickets += votingRecord.Count;
            tickets.HistoryVotedTickets += votingRecord.Count;
            State.TicketsMap[voterPublicKey.ToStringValue()] = tickets;

            // Add the transaction id of this voting record to the tickets information of the candidate.
            var candidateTickets = State.TicketsMap[candidatePublicKey.ToStringValue()];
            if (candidateTickets != null)
            {
                candidateTickets.VoteFromTransactions.Add(votingRecord.TransactionId);
            }
            else
            {
                candidateTickets = new Tickets();
                candidateTickets.VoteFromTransactions.Add(votingRecord.TransactionId);
            }

            candidateTickets.ObtainedTickets += votingRecord.Count;
            candidateTickets.HistoryObtainedTickets += votingRecord.Count;
            State.TicketsMap[candidatePublicKey.ToStringValue()] = candidateTickets;

            // Update the amount of votes (voting records of whole system).
            State.VotesCountField.Value += 1;

            // Update the amount of tickets.
            State.TicketsCountField.Value += 1;

            // Add this voting record to voting records map.
            State.VotingRecordsMap[votingRecord.TransactionId] = votingRecord;

            // Tell Dividends Contract to add weights for this voting record.
            State.DividendContract.AddWeights(votingRecord.Weight, currentTermNumber + 1);

            Context.LogDebug(() => $"Weights of vote {votingRecord.TransactionId.ToHex()}: {votingRecord.Weight}");

            return Context.TransactionId.ToHex();
        }

        // ReSharper disable once PossibleNullReferenceException
        public ActionResult ReceiveDividendsByTransactionId(string transactionId)
        {
            var votingRecord = State.VotingRecordsMap[Hash.LoadHex(transactionId)];

            Assert(votingRecord != null,
                ContractErrorCode.GetErrorMessage(ContractErrorCode.NotFound, "Voting record not found."));

            Assert(votingRecord.From == Context.RecoverPublicKey().ToHex(),
                ContractErrorCode.GetErrorMessage(ContractErrorCode.NoPermission,
                    "No permission to receive."));

            State.DividendContract.TransferDividends(votingRecord);

            return new ActionResult {Success = true};
        }

        // ReSharper disable once PossibleNullReferenceException
        public ActionResult ReceiveAllDividends()
        {
            var tickets = State.TicketsMap[Context.RecoverPublicKey().ToHex().ToStringValue()];

            Assert(tickets != null,
                ContractErrorCode.GetErrorMessage(ContractErrorCode.NotFound, "Tickets information not found."));

            Assert(tickets.VoteToTransactions.Any(),
                ContractErrorCode.GetErrorMessage(ContractErrorCode.NotFound, "Voting records not found."));

            foreach (var transactionId in tickets.VoteToTransactions)
            {
                var votingRecord = State.VotingRecordsMap[transactionId];
                Assert(votingRecord != null,
                    ContractErrorCode.GetErrorMessage(ContractErrorCode.NotFound, "Voting record not found."));
                State.DividendContract.TransferDividends(votingRecord);
            }

            return new ActionResult {Success = true};
        }

        // ReSharper disable once PossibleNullReferenceException
        public Tickets WithdrawByTransactionId(string transactionId)
        {
            var votingRecord = State.VotingRecordsMap[Hash.LoadHex(transactionId)];

            Assert(votingRecord != null,
                ContractErrorCode.GetErrorMessage(ContractErrorCode.NotFound, "Voting record not found."));

            Assert(!votingRecord.IsWithdrawn,
                ContractErrorCode.GetErrorMessage(ContractErrorCode.InvalidOperation,
                    "This voting record has already withdrawn."));

            Assert(votingRecord.UnlockAge > CurrentAge,
                ContractErrorCode.GetErrorMessage(ContractErrorCode.InvalidOperation,
                    "This voting record can't withdraw for now."));

            // Update voting record map.
            var blockchainStartTimestamp = State.BlockchainStartTimestamp.Value;
            votingRecord.WithdrawTimestamp =
                blockchainStartTimestamp.ToDateTime().AddDays(CurrentAge).ToTimestamp();
            votingRecord.IsWithdrawn = true;
            State.VotingRecordsMap[Hash.LoadHex(transactionId)] = votingRecord;

            // Update total tickets count.
            var ticketsCount = State.TicketsCountField.Value;
            ticketsCount -= votingRecord.Count;
            State.TicketsCountField.Value = ticketsCount;

            // Update tickets number of this voter.
            var ticketsOfVoter = State.TicketsMap[votingRecord.From.ToStringValue()];
            if (ticketsOfVoter != null)
            {
                ticketsOfVoter.VotedTickets -= votingRecord.Count;
                State.TicketsMap[votingRecord.From.ToStringValue()] = ticketsOfVoter;
            }

            // Update tickets number of related candidate.
            var ticketsOfCandidate = State.TicketsMap[votingRecord.To.ToStringValue()];
            if (ticketsOfCandidate != null)
            {
                ticketsOfCandidate.ObtainedTickets -= votingRecord.Count;
                State.TicketsMap[votingRecord.To.ToStringValue()] = ticketsOfCandidate;
            }

            // Sub weight.
            State.DividendContract.SubWeights(votingRecord.Weight, State.CurrentTermNumberField.Value);
            // Transfer token back to voter.
            State.TokenContract.Unlock(Context.Sender, votingRecord.Count);

            return State.TicketsMap[votingRecord.From.ToStringValue()];
        }

        // ReSharper disable PossibleNullReferenceException
        public Tickets WithdrawAll()
        {
            var voterPublicKey = Context.RecoverPublicKey().ToHex();
            var ticketsCount = State.TicketsCountField.Value;
            var withdrawnAmount = 0UL;
            var candidatesVotesDict = new Dictionary<string, ulong>();

            var tickets = State.TicketsMap[voterPublicKey.ToStringValue()];

            Assert(tickets != null,
                ContractErrorCode.GetErrorMessage(ContractErrorCode.NotFound, "Tickets information not found."));

            foreach (var transactionId in tickets.VoteToTransactions)
            {
                var votingRecord = State.VotingRecordsMap[transactionId];

                Assert(votingRecord != null,
                    ContractErrorCode.GetErrorMessage(ContractErrorCode.NotFound, "Voting record not found."));

                if (votingRecord.UnlockAge > CurrentAge)
                {
                    // Just check next one, no need to assert.
                    continue;
                }

                // Update voting record map.
                var blockchainStartTimestamp = State.BlockchainStartTimestamp.Value;
                votingRecord.WithdrawTimestamp =
                    blockchainStartTimestamp.ToDateTime().AddMinutes(CurrentAge).ToTimestamp();
                votingRecord.IsWithdrawn = true;
                State.VotingRecordsMap[votingRecord.TransactionId] = votingRecord;

                // Prepare data for updating tickets map.
                withdrawnAmount += votingRecord.Count;
                if (candidatesVotesDict.ContainsKey(votingRecord.To))
                {
                    candidatesVotesDict[votingRecord.To] += votingRecord.Count;
                }
                else
                {
                    candidatesVotesDict.Add(votingRecord.To, votingRecord.Count);
                }

                State.TokenContract.Unlock(Context.Sender, votingRecord.Count);
                State.DividendContract.SubWeights(votingRecord.Weight, State.CurrentTermNumberField.Value);
            }

            ticketsCount -= withdrawnAmount;
            State.TicketsCountField.Value = ticketsCount;

            tickets.VotedTickets -= withdrawnAmount;
            State.TicketsMap[voterPublicKey.ToStringValue()] = tickets;

            foreach (var candidateVote in candidatesVotesDict)
            {
                var ticketsOfCandidate = State.TicketsMap[candidateVote.Key.ToStringValue()];
                Assert(ticketsOfCandidate != null,
                    ContractErrorCode.GetErrorMessage(ContractErrorCode.NotFound,
                        $"Tickets information of {candidateVote.Key} not found."));
                ticketsOfCandidate.ObtainedTickets -= candidateVote.Value;
                State.TicketsMap[candidateVote.Key.ToStringValue()] = ticketsOfCandidate;
            }

            return State.TicketsMap[voterPublicKey.ToStringValue()];
        }
    }
}