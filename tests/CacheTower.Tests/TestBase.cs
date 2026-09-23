using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CacheTower.Tests.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests
{
	public abstract class TestBase
	{
		/// <summary>
		/// Marks the current integration test as inconclusive when no Redis server is reachable.
		/// </summary>
		protected static void RequireRedis()
		{
			if (!RedisHelper.IsAvailable())
			{
				Assert.Inconclusive($"No Redis server is available at {RedisHelper.Endpoint} (set REDIS_ENDPOINT to override).");
			}
		}

		/// <summary>
		/// Marks the current integration test as inconclusive when no MongoDB server is reachable.
		/// </summary>
		protected static void RequireMongoDb()
		{
			if (!MongoDbHelper.IsAvailable())
			{
				Assert.Inconclusive($"No MongoDB server is available at {MongoDbHelper.ConnectionString} (set MONGODB_URI to override).");
			}
		}
	}
}
