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
		LinkedList<WebConnection> connections;
		Queue queue;
		static int next_id;
		int id = ++next_id;

		public WebConnectionGroup (ServicePoint sPoint, string name)
		{
			this.sPoint = sPoint;
			this.name = name;
			connections = new LinkedList<WebConnection> ();
			queue = new Queue ();
		}

		public void Close ()
		{
			//TODO: what do we do with the queue? Empty it out and abort the requests?
			//TODO: abort requests or wait for them to finish
			lock (connections) {
				foreach (var cnc in connections)
					cnc.Close (false);
				connections.Clear ();
			}
		}

		public WebConnection GetConnection (HttpWebRequest request)
		{
			lock (connections) {
				return CreateOrReuseConnection (request);
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

		WebConnection FindIdleConnection ()
		{
			foreach (var cnc in connections) {
				if (cnc.Busy)
					continue;

				connections.Remove (cnc);
				connections.AddFirst (cnc);
				return cnc;
			}

			return null;
		}

		WebConnection CreateOrReuseConnection (HttpWebRequest request)
		{
			var cnc = FindIdleConnection ();
			Debug ("WCG CREATE OR REUSE: {0}", cnc != null);
			if (cnc != null) {
				PrepareSharingNtlm (cnc, request);
				return cnc;
			}

			Debug ("WCG CREATE OR REUSE #2: {0} {1}", sPoint.ConnectionLimit, connections.Count);

			if (sPoint.ConnectionLimit > connections.Count) {
				cnc = new WebConnection (this, sPoint);
				connections.AddFirst (cnc);
				return cnc;
			}

			Debug ("WCG CREATE OR REUSE #3");

			cnc = connections.Last.Value;
			connections.Remove (cnc);
			connections.AddFirst (cnc);
			return cnc;
		}

		public string Name {
			get { return name; }
		}

		internal Queue Queue {
			get { return queue; }
		}
		
	}
}

