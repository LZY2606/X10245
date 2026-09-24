using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MongoDB.Driver;
using MongoFramework;

namespace CacheTower.Tests.Utils
{
	public static class MongoDbHelper
	{
		public static string ConnectionString => Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost";

		private static bool? IsEndpointAvailable;

		/// <summary>
		/// Whether a MongoDB server can be reached at <see cref="ConnectionString"/>. Probed once and cached.
		/// </summary>
		public static bool IsAvailable
		{
			get
			{
				if (IsEndpointAvailable is null)
				{
					try
					{
						var settings = MongoClientSettings.FromUrl(new MongoUrl(ConnectionString));
						settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
						var client = new MongoClient(settings);
						client.ListDatabaseNames().ToList();
						IsEndpointAvailable = true;
					}
					catch
					{
						IsEndpointAvailable = false;
					}
				}
				return IsEndpointAvailable.Value;
			}
		}

		/// <summary>
		/// Marks the current test as inconclusive when MongoDB is unavailable and no explicit connection string was configured.
		/// </summary>
		public static void ThrowIfUnavailable()
		{
			if (!IsAvailable && Environment.GetEnvironmentVariable("MONGODB_URI") is null)
			{
				Assert.Inconclusive($"MongoDB is not available at {ConnectionString}; skipping integration test.");
			}
		}

		public static string GetDatabaseName()
		{
			return "CacheTowerTests";
		}

		public static IMongoDbConnection GetConnection()
		{
			ThrowIfUnavailable();
			var urlBuilder = new MongoUrlBuilder(ConnectionString)
			{
				DatabaseName = GetDatabaseName()
			};
			return MongoDbConnection.FromUrl(urlBuilder.ToMongoUrl());
		}

		public static async Task DropDatabaseAsync()
		{
			ThrowIfUnavailable();
			await GetConnection().Client.DropDatabaseAsync(GetDatabaseName());
		}
		public static void DropDatabase()
		{
			ThrowIfUnavailable();
			GetConnection().Client.DropDatabase(GetDatabaseName());
		}
	}
}
