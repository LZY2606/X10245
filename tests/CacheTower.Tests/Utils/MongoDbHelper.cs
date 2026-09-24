using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using MongoDB.Driver;
using MongoFramework;

namespace CacheTower.Tests.Utils
{
	public static class MongoDbHelper
	{
		public static string ConnectionString => Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost";

		private static readonly Lazy<bool> Available = new(() =>
		{
			try
			{
				var settings = MongoClientSettings.FromConnectionString(ConnectionString);
				settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
				var client = new MongoClient(settings);
				using (client.ListDatabaseNames())
				{
					return true;
				}
			}
			catch
			{
				return false;
			}
		});

		/// <summary>
		/// Whether a MongoDB server is reachable at <see cref="ConnectionString"/>. Used to skip integration tests when no server is available.
		/// </summary>
		public static bool IsAvailable => Available.Value;

		public static string GetDatabaseName()
		{
			return "CacheTowerTests";
		}

		public static IMongoDbConnection GetConnection()
		{
			var urlBuilder = new MongoUrlBuilder(ConnectionString)
			{
				DatabaseName = GetDatabaseName()
			};
			return MongoDbConnection.FromUrl(urlBuilder.ToMongoUrl());
		}

		public static async Task DropDatabaseAsync()
		{
			await GetConnection().Client.DropDatabaseAsync(GetDatabaseName());
		}
		public static void DropDatabase()
		{
			GetConnection().Client.DropDatabase(GetDatabaseName());
		}
	}
}
