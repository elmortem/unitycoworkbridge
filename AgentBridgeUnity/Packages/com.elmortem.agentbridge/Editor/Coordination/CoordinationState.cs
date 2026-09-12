using System;
using System.Collections.Generic;

namespace AgentBridge.Coordination
{
	[Serializable]
	public class CoordinationState
	{
		public int SchemaVersion = CoordinationLimits.SchemaVersion;
		public string ProjectId = "";
		public string Epoch = "";
		public long Revision;
		public long NextTicket = 1;
		public long NextRequestNumber = 1;

		// The editor process incarnation that last touched the state. A different value means the
		// editor restarted: windows are interrupted, edit grants and scopes are not.
		public string EditorIncarnation = "";

		public List<CoordinationParticipant> Participants = new List<CoordinationParticipant>();
		public List<CoordinationRequest> Requests = new List<CoordinationRequest>();
		public List<CoordinationGrant> Grants = new List<CoordinationGrant>();
		public List<CoordinationTombstone> Tombstones = new List<CoordinationTombstone>();

		public void Normalize()
		{
			ProjectId = CoordinationText.Safe(ProjectId);
			Epoch = CoordinationText.Safe(Epoch);
			EditorIncarnation = CoordinationText.Safe(EditorIncarnation);
			if (NextTicket < 1)
			{
				NextTicket = 1;
			}

			if (NextRequestNumber < 1)
			{
				NextRequestNumber = 1;
			}

			if (Participants == null)
			{
				Participants = new List<CoordinationParticipant>();
			}

			if (Requests == null)
			{
				Requests = new List<CoordinationRequest>();
			}

			if (Grants == null)
			{
				Grants = new List<CoordinationGrant>();
			}

			if (Tombstones == null)
			{
				Tombstones = new List<CoordinationTombstone>();
			}

			foreach (CoordinationParticipant participant in Participants)
			{
				participant.Normalize();
			}

			foreach (CoordinationRequest request in Requests)
			{
				request.Normalize();
			}

			foreach (CoordinationGrant grant in Grants)
			{
				grant.Normalize();
			}

			foreach (CoordinationTombstone tombstone in Tombstones)
			{
				tombstone.Normalize();
			}
		}

		public CoordinationParticipant FindParticipant(string session)
		{
			foreach (CoordinationParticipant participant in Participants)
			{
				if (string.Equals(participant.Session, session, StringComparison.Ordinal))
				{
					return participant;
				}
			}

			return null;
		}

		public CoordinationRequest FindRequest(string requestId)
		{
			foreach (CoordinationRequest request in Requests)
			{
				if (string.Equals(request.Id, requestId, StringComparison.Ordinal))
				{
					return request;
				}
			}

			return null;
		}

		public CoordinationRequest FindRequestByUuid(string session, string uuid)
		{
			foreach (CoordinationRequest request in Requests)
			{
				if (string.Equals(request.Session, session, StringComparison.Ordinal)
					&& string.Equals(request.Uuid, uuid, StringComparison.Ordinal))
				{
					return request;
				}
			}

			return null;
		}

		public CoordinationGrant FindGrantByToken(string token)
		{
			if (string.IsNullOrEmpty(token))
			{
				return null;
			}

			foreach (CoordinationGrant grant in Grants)
			{
				if (string.Equals(grant.Token, token, StringComparison.Ordinal))
				{
					return grant;
				}
			}

			return null;
		}

		public CoordinationGrant FindGrantBySession(string session, string kind)
		{
			foreach (CoordinationGrant grant in Grants)
			{
				if (!string.Equals(grant.Session, session, StringComparison.Ordinal))
				{
					continue;
				}

				if (kind == null || string.Equals(grant.Kind, kind, StringComparison.Ordinal))
				{
					return grant;
				}
			}

			return null;
		}

		public CoordinationGrant FindWindowGrant()
		{
			foreach (CoordinationGrant grant in Grants)
			{
				if (CoordinationLimits.IsWindowKind(grant.Kind))
				{
					return grant;
				}
			}

			return null;
		}

		public CoordinationTombstone FindTombstone(string session, string key)
		{
			foreach (CoordinationTombstone tombstone in Tombstones)
			{
				if (string.Equals(tombstone.Session, session, StringComparison.Ordinal)
					&& string.Equals(tombstone.Key, key, StringComparison.Ordinal))
				{
					return tombstone;
				}
			}

			return null;
		}
	}
}
