//
// System.Net.WebConnectionGroup
//
// Authors:
//	Gonzalo Paniagua Javier (gonzalo@ximian.com)
//
// (C) 2003 Ximian, Inc (http://www.ximian.com)
// Copyright 2011 Xamarin, Inc (http://www.xamarin.com)
//

//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using System.Net.Configuration;
using System.Net.Sockets;
using System.Diagnostics;

namespace System.Net
{
	class WebConnectionGroup
	{
		ServicePoint sPoint;
		string name;
		LinkedList<ConnectionState> connections;
		Queue queue;
		bool closing;

		static int next_id;
		int id = ++next_id;

		public WebConnectionGroup (ServicePoint sPoint, string name)
		{
			this.sPoint = sPoint;
			this.name = name;
			connections = new LinkedList<ConnectionState> ();
			queue = new Queue ();
		}

		public event EventHandler ConnectionClosed;

		void OnConnectionClosed ()
		{
			if (ConnectionClosed != null)
				ConnectionClosed (this, null);
		}

		public void Close ()
		{
			//TODO: what do we do with the queue? Empty it out and abort the requests?
			//TODO: abort requests or wait for them to finish
			lock (sPoint.SyncRoot) {
				closing = true;
				foreach (var cnc in connections) {
					if (cnc.Connection == null)
						continue;
					cnc.Connection.Close (false);
					cnc.Connection = null;
					OnConnectionClosed ();
				}
				connections.Clear ();
			}
		}

		public WebConnection GetConnection (HttpWebRequest request, out bool created)
		{
			lock (sPoint.SyncRoot) {
				return CreateOrReuseConnection (request, out created);
			}
		}

		static void PrepareSharingNtlm (WebConnection cnc, HttpWebRequest request)
		{
			if (!cnc.NtlmAuthenticated)
				return;

			bool needs_reset = false;
			NetworkCredential cnc_cred = cnc.NtlmCredential;

			bool isProxy = (request.Proxy != null && !request.Proxy.IsBypassed (request.RequestUri));
			ICredentials req_icreds = (!isProxy) ? request.Credentials : request.Proxy.Credentials;
			NetworkCredential req_cred = (req_icreds != null) ? req_icreds.GetCredential (request.RequestUri, "NTLM") : null;

			if (cnc_cred == null || req_cred == null ||
				cnc_cred.Domain != req_cred.Domain || cnc_cred.UserName != req_cred.UserName ||
				cnc_cred.Password != req_cred.Password) {
				needs_reset = true;
			}

			if (!needs_reset) {
				bool req_sharing = request.UnsafeAuthenticatedConnectionSharing;
				bool cnc_sharing = cnc.UnsafeAuthenticatedConnectionSharing;
				needs_reset = (req_sharing == false || req_sharing != cnc_sharing);
			}
			if (needs_reset) {
				cnc.Close (false); // closes the authenticated connection
				cnc.ResetNtlm ();
			}
		}

		[Conditional ("DEBUG")]
		void Debug (string message, params object[] args)
		{
			Console.WriteLine ("[{0}:{1}]: {2}", Thread.CurrentThread.ManagedThreadId, id, string.Format (message, args));
		}

		ConnectionState FindIdleConnection ()
		{
			foreach (var cnc in connections) {
				if (cnc.Busy  || cnc.Connection == null)
					continue;

				connections.Remove (cnc);
				connections.AddFirst (cnc);
				return cnc;
			}

			return null;
		}

		WebConnection CreateOrReuseConnection (HttpWebRequest request, out bool created)
		{
			var cnc = FindIdleConnection ();
			Debug ("WCG CREATE OR REUSE: {0}", cnc != null);
			if (cnc != null) {
				created = false;
				PrepareSharingNtlm (cnc.Connection, request);
				return cnc.Connection;
			}

			Debug ("WCG CREATE OR REUSE #2: {0} {1}", sPoint.ConnectionLimit, connections.Count);

			if (sPoint.ConnectionLimit > connections.Count) {
				created = true;
				cnc = new ConnectionState (this);
				connections.AddFirst (cnc);
				return cnc.Connection;
			}

			Debug ("WCG CREATE OR REUSE #3");

			created = false;
			cnc = connections.Last.Value;
			connections.Remove (cnc);
			connections.AddFirst (cnc);
			return cnc.Connection;
		}

		public string Name {
			get { return name; }
		}

		internal Queue Queue {
			get { return queue; }
		}

		internal bool TryRecycle (TimeSpan maxIdleTime, ref DateTime idleSince, bool debug)
		{
		again:
			bool recycled;
			List<WebConnection> connectionsToClose = null;

			lock (sPoint.SyncRoot) {
				if (closing) {
					idleSince = DateTime.MinValue;
					return true;
				}

				int count = 0;
				var list = new List<ConnectionState> (connections);
				foreach (var cnc in list) {
					if (cnc.Connection == null) {
						connections.Remove (cnc);
						OnConnectionClosed ();
						continue;
					}

					++count;
					if (cnc.Busy)
						continue;

					if (debug)
						sPoint.Debug ("CHECK IDLE: {0} {1} {2}", DateTime.UtcNow - cnc.IdleSince, count, sPoint.ConnectionLimit);

					if (count <= sPoint.ConnectionLimit && DateTime.UtcNow - cnc.IdleSince < maxIdleTime) {
						if (cnc.IdleSince > idleSince)
							idleSince = cnc.IdleSince;
						continue;
					}

					if (debug)
						sPoint.Debug ("CLOSE IDLE CONNECTION: {0}", cnc.Connection);

					/*
					 * Do not call WebConnection.Close() while holding the ServicePoint.SyncRoot lock
					 * as this could deadlock when attempting to take the WebConnection lock.
					 * 
					 */

					if (connectionsToClose == null)
						connectionsToClose = new List<WebConnection> ();
					connectionsToClose.Add (cnc.Connection);
					cnc.Connection = null;
				}

				recycled = connections.Count == 0;
			}

			// Did we find anything that can be closed?
			if (connectionsToClose == null)
				return recycled;

			// Ok, let's get rid of these!
			foreach (var cnc in connectionsToClose)
				cnc.Close (false);

			// Re-take the lock, then remove them fromt he connection list.
			goto again;
		}

		class ConnectionState : IWebConnectionState {
			public WebConnection Connection;

			public WebConnectionGroup Group {
				get;
				private set;
			}

			public ServicePoint ServicePoint {
				get { return Group.sPoint; }
			}

			bool busy;
			DateTime idleSince;

			public bool Busy {
				get {
					lock (ServicePoint.SyncRoot) {
						return busy;
					}
				}
			}

			public DateTime IdleSince {
				get {
					lock (ServicePoint.SyncRoot) {
						return idleSince;
					}
				}
			}

			public bool TrySetBusy ()
			{
				lock (ServicePoint.SyncRoot) {
					if (busy)
						return false;
					busy = true;
					idleSince = DateTime.UtcNow + TimeSpan.FromDays (3650);
					return true;
				}
			}

			public void SetIdle ()
			{
				lock (ServicePoint.SyncRoot) {
					busy = false;
					idleSince = DateTime.UtcNow;
				}
			}

			public ConnectionState (WebConnectionGroup group)
			{
				Group = group;
				idleSince = DateTime.UtcNow;
				Connection = new WebConnection (this, group.sPoint);
			}
		}
		
	}
}

