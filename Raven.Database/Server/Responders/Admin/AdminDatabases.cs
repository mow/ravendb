﻿// -----------------------------------------------------------------------
//  <copyright file="AdminDatabases.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System;
using System.Security.Cryptography;
using System.Text;
using Raven.Abstractions.Data;
using Raven.Database.Server.Abstractions;
using Raven.Database.Extensions;
using Raven.Json.Linq;
using System.Linq;
using Raven.Abstractions.Extensions;

namespace Raven.Database.Server.Responders.Admin
{
	public class AdminDatabases : AdminResponder
	{
		public override string[] SupportedVerbs
		{
			get { return new[] {"GET", "PUT", "DELETE"}; }
		}

		public override string UrlPattern
		{
			get { return "^/admin/databases/(.+)"; }
		}

		public override void RespondToAdmin(IHttpContext context)
		{
			if (EnsureSystemDatabase(context) == false)
				return;

			var match = urlMatcher.Match(context.GetRequestUrl());
			var db = Uri.UnescapeDataString(match.Groups[1].Value);

			DatabaseDocument dbDoc;
			switch (context.Request.HttpMethod)
			{
				case "GET":
					var document = Database.Get("Raven/Databases/" + db, null);
					if (document == null)
					{
						context.SetStatusToNotFound();
						return;
					}
					dbDoc = document.DataAsJson.JsonDeserialization<DatabaseDocument>();
					dbDoc.Id = db;	
					server.Unprotect(dbDoc);
					context.WriteJson(dbDoc);
					break;
				case "PUT":
					dbDoc = context.ReadJsonObject<DatabaseDocument>();
					server.Protect(dbDoc);
					var json = RavenJObject.FromObject(dbDoc);
					json.Remove("Id");

					Database.Put("Raven/Databases/" + db, null, json, new RavenJObject(), null);
					break;
				case "DELETE":
					Database.Delete("Raven/Databases/" + db, null, null);
					break;
			}
		}
	}
}