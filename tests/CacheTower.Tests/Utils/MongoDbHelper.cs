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

		private static readonly Lazy<bool> Availability = new(ProbeAvailability);

		/// <summary>
		/// Whether a MongoDB server is reachable at <see cref="ConnectionString"/>. Probed once with a short timeout.
		/// </summary>
		public static bool IsAvailable => Availability.Value;

		private static bool ProbeAvailability()
		{
			try
			{
				var settings = MongoClientSettings.FromConnectionString(ConnectionString);
				settings.ServerSelectionTimeout = TimeSpan.FromSeconds(1);
				settings.ConnectTimeout = TimeSpan.FromSeconds(1);
				var client = new MongoClient(settings);
				client.ListDatabaseNames();
				return true;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Marks the current test as inconclusive when no MongoDB server is available, allowing
		/// the test suite to run on machines without a MongoDB service.
		/// </summary>
		public static void ThrowIfUnavailable()
		{
			if (!IsAvailable)
			{
				Assert.Inconclusive($"MongoDB is not available at {ConnectionString}; set the MONGODB_URI environment variable to run this test.");
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
