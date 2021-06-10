﻿using System;
using System.Linq;
using System.Collections.Concurrent;
using ClearScada.Client;
using ClearScada.Client.Advanced;

/// <summary>
/// Create one of these to feed data out. 
/// The class uses a variable NextKey to hold next point change id, used to identify incoming data
/// It uses a concurrent dictionary of PointInfo objects to retain information about monitored points
/// There are concurrent queues of point data changes and configuration changes.
/// Concurrent queues are used in order to avoid any async ClearSCADA API calls, as the API is not
/// thread-safe.
/// Four function/action callbacks need to be passed in to handle data change, shutdown and new point
/// filtering. 
/// </summary>
namespace DataFeeder
{
	static class FeederEngine
	{
		static IServer AdvConnection;

		// Dict of PointInfo
		private static ConcurrentDictionary<int, PointInfo> PointDictionary = new ConcurrentDictionary<int, PointInfo>();
		private static int NextKey = 1;
		private static ServerState CurrentServerState = ServerState.None;
		// Dict of Updates
		private static ConcurrentQueue<TagUpdate> UpdateQueue = new ConcurrentQueue<TagUpdate>();
		// Dict of Config changes
		private static ConcurrentQueue<ObjectUpdateEventArgs> ConfigQueue = new ConcurrentQueue<ObjectUpdateEventArgs>();
	
		public static int UpdateIntervalSec; // Used for all points added to the engine

		private static Action<string, int, string, double, DateTimeOffset, int> ProcessNewData;
		private static Action<string, int, string> ProcessNewConfig;
		private static Action Shutdown;
		private static Func<string, bool> FilterNewPoint;

		/// <summary>
		/// Acts as the initialiser/constructor
		/// </summary>
		/// <param name="_AdvConnection">Database connection</param>
		/// <param name="_UpdateIntervalSec">See strong warnings in sample about setting this too low.</param>
		/// <param name="_ProcessNewData">Callback</param>
		/// <param name="_ProcessNewConfig">Callback</param>
		/// <param name="_Shutdown">Callback</param>
		/// <param name="_FilterNewPoint">Callback</param>
		/// <returns></returns>
		public static bool Connect(IServer _AdvConnection,
									int _UpdateIntervalSec,
									Action<string, int, string, double, DateTimeOffset, int> _ProcessNewData,
									Action<string, int, string> _ProcessNewConfig,
									Action _Shutdown,
									Func<string, bool> _FilterNewPoint)
		{
			AdvConnection = _AdvConnection;
			UpdateIntervalSec = _UpdateIntervalSec;

			// Check server is valid
			CurrentServerState = AdvConnection.GetServerState().State;
			if (!(CurrentServerState == ServerState.Main) && !(CurrentServerState == ServerState.Standby))
			{
				return false;
			}
			// Set action callbacks in application code
			ProcessNewData = _ProcessNewData;
			ProcessNewConfig = _ProcessNewConfig;
			Shutdown = _Shutdown;
			FilterNewPoint = _FilterNewPoint;

			// Set event callback
			AdvConnection.TagsUpdated += TagUpdateEvent;

			// Set config change callback
			var WaitFor = new string[] { "CDBPoint", "CAccumulatorBase" };
			AdvConnection.ObjectUpdated += ObjUpdateEvent;
			AdvConnection.AdviseObjectUpdates(false, WaitFor);

			// Disconnect Callbacks
			AdvConnection.StateChanged += DBStateChangeEvent;

			return true;
		}

		/// <summary>
		/// Called when server state changes, and the creator of this object must drop it and create a new one
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="stateChange"></param>
		static void DBStateChangeEvent( object sender, StateChangeEventArgs stateChange)
		{
			if (stateChange.StateDetails.State != CurrentServerState)
			{
				EngineShutdown();
			}
		}
		/// <summary>
		/// Called if there is a comms error, deletes dict and calls back
		/// Consider here recording the last retrieve dates of each point
		/// </summary>
		static void EngineShutdown()
		{
			// Remove all items and advise caller that database has shut down
			foreach (var Key in PointDictionary.Keys)
			{
				PointDictionary.TryRemove(Key, out PointInfo point);
			}
			Shutdown();
		}

		/// <summary>
		/// Useful info
		/// </summary>
		/// <returns></returns>
		public static int SubscriptionCount()
		{
			return PointDictionary.Count();
		}
		public static int ProcessQueueCount()
		{
			return UpdateQueue.Count();
		}
		public static int ConfigQueueCount()
		{
			return ConfigQueue.Count();
		}

		/// <summary>
		/// Add a new point/accumulator to be monitored
		/// </summary>
		/// <param name="FullName">Object name string - FullName</param>
		/// <param name="LastChange">If historic object, then data retrieval starts at this time</param>
		/// <returns></returns>
		public static bool AddSubscription( string FullName, DateTimeOffset LastChange)
		{
			bool s = PointDictionary.TryAdd(NextKey, new PointInfo(NextKey, FullName, UpdateIntervalSec, LastChange, AdvConnection, ProcessNewData));
			if (s)
			{
				NextKey++;
			}
			return s;
		}

		/// <summary>
		/// Caller should make regular calls to this to process items off the queues
		/// </summary>
		/// <returns></returns>
		public static long ProcessUpdates()
		{
			long UpdateCount = 0;

			// If queued updates
			//if (UpdateQueue.Count > 0)
				//Console.WriteLine("Queued Updates: " + UpdateQueue.Count.ToString());
			DateTimeOffset ProcessStartTime = DateTimeOffset.UtcNow;
			while (UpdateQueue.Count > 0)
			{
				if (UpdateQueue.TryDequeue(out TagUpdate update))
				{
					// Find point
					if (PointDictionary.TryGetValue(update.Id, out PointInfo info))
					{
						UpdateCount += info.ReadHistoric(AdvConnection, update);
					}
					//Console.WriteLine("Queue now " + UpdateQueue.Count.ToString()); 
				}
				if ((DateTimeOffset.UtcNow - ProcessStartTime).TotalSeconds > 1)
				{
					//Console.WriteLine("End after 1sec");
					break;
				}
			}

			// If queued config
			//if (ConfigQueue.Count > 0)
			//	Console.WriteLine("Queued Config: " + ConfigQueue.Count.ToString());
			DateTimeOffset ConfigStartTime = DateTimeOffset.UtcNow;
			while (ConfigQueue.Count > 0)
			{
				UpdateCount++; // Include config changes in this count
				if (ConfigQueue.TryDequeue(out ObjectUpdateEventArgs objupdate))
				{
					if (objupdate.UpdateType == ObjectUpdateType.Created)
					{
						// This is run for all new points - use filter to include user's desired points, otherwise everything new would be added
						var newpoint = AdvConnection.LookupObject(new ObjectId(objupdate.ObjectId));
						if (newpoint != null && FilterNewPoint(newpoint.FullName))
						{
							PointDictionary.TryAdd(NextKey, new PointInfo(NextKey, newpoint.FullName, UpdateIntervalSec, DateTimeOffset.MinValue, AdvConnection, ProcessNewData));
							NextKey++;
							Console.WriteLine("Added new point: " + newpoint.FullName);
							ProcessNewConfig("Created", objupdate.ObjectId, newpoint.FullName);
						}
					}
					else
					{
						// Find point - will need to look at the full list
						foreach (var Info in PointDictionary)
						{
							if (Info.Value.PointId == objupdate.ObjectId)
							{
								// We have the point which changed
								switch (objupdate.UpdateType)
								{
									case ObjectUpdateType.Modified:
										// Could have historic enabled/removed, so we need to unsub and resub
										if (!PointDictionary.TryRemove(Info.Key, out PointInfo modified))
										{
											Console.WriteLine("Error removing modified point: " + objupdate.ObjectId);
										}
										Info.Value.Dispose();
										var modpoint = AdvConnection.LookupObject(new ObjectId(objupdate.ObjectId));
										if (modpoint != null)
										{
											PointDictionary.TryAdd(NextKey, new PointInfo(NextKey, modpoint.FullName, UpdateIntervalSec, modified.LastChange, AdvConnection, ProcessNewData));
											NextKey++;
											Console.WriteLine("Replaced point: " + modpoint.FullName + ", from: " + modified.LastChange.ToString());
											ProcessNewConfig( "Modified", objupdate.ObjectId, modpoint.FullName);
										}
										break;
									case ObjectUpdateType.Deleted:
										if (!PointDictionary.TryRemove(Info.Key, out PointInfo removed))
										{
											Console.WriteLine("Error removing deleted point: " + objupdate.ObjectId);
										}
										Info.Value.Dispose();
										break;
									case ObjectUpdateType.Renamed:
										var renpoint = AdvConnection.LookupObject(new ObjectId(objupdate.ObjectId));
										if (renpoint != null)
										{
											Info.Value.Rename(renpoint.FullName);
											Console.WriteLine("Renamed to: " + renpoint.FullName);
											ProcessNewConfig("Renamed", objupdate.ObjectId, renpoint.FullName);
										}
										break;
									default:
										break;
								}
								break;
							}
						}
					}
					if ((DateTimeOffset.UtcNow - ConfigStartTime).TotalSeconds > 1)
					{
						//Console.WriteLine("End after 1sec");
						break;
					}

				}
			}
			return UpdateCount;
		}

		// Callbacks - Tag Update
		static void TagUpdateEvent(object sender, TagsUpdatedEventArgs EventArg)
		{
			foreach (var Update in EventArg.Updates)
			{
				UpdateQueue.Enqueue(Update);
				//Console.WriteLine("Tag Update: " + Update.Id.ToString() + ", " + Update.Value.ToString());
			}
		}
		// Object Update (configuration)
		static void ObjUpdateEvent(object sender, ClearScada.Client.ObjectUpdateEventArgs EventArg)
		{
			ConfigQueue.Enqueue(EventArg);
			//Console.WriteLine("Object Config: " + EventArg.ObjectId.ToString() + "  " + EventArg.ObjectName + "  " + EventArg.UpdateType + "  " + EventArg.ObjectClass);
		}

	}

}
