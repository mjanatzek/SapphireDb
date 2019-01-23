﻿using RealtimeDatabase.Internal;
using RealtimeDatabase.Models;
using RealtimeDatabase.Models.Prefilter;
using RealtimeDatabase.Models.Responses;
using RealtimeDatabase.Websocket.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RealtimeDatabase.Websocket
{
    public class WebsocketChangeNotifier
    {
        private readonly WebsocketConnectionManager connectionManager;
        private readonly DbContextAccesor dbContextAccessor;

        public WebsocketChangeNotifier(WebsocketConnectionManager connectionManager, DbContextAccesor dbContextAccessor)
        {
            this.connectionManager = connectionManager;
            this.dbContextAccessor = dbContextAccessor;
        }

        public Task HandleChanges(List<ChangeResponse> changes)
        {
            RealtimeDbContext db = dbContextAccessor.GetContext();

            foreach (WebsocketConnection connection in connectionManager.connections)
            {
                foreach (IGrouping<string, CollectionSubscription> subscriptionGrouping in 
                    connection.Subscriptions.GroupBy(s => s.CollectionName))
                {
                    HandleSubscription(subscriptionGrouping, changes, connection, db);
                }
            }

            return Task.CompletedTask;
        }

        private void HandleSubscription(IGrouping<string, CollectionSubscription> subscriptionGrouping, List<ChangeResponse> changes,
            WebsocketConnection connection, RealtimeDbContext db)
        {
            IEnumerable<ChangeResponse> relevantChanges =
                        changes.Where(c => c.CollectionName == subscriptionGrouping.Key);

            KeyValuePair<Type, string> property = db.sets
                .FirstOrDefault(v => v.Value.ToLowerInvariant() == subscriptionGrouping.Key);

            if (property.Key != null)
            {
                relevantChanges = relevantChanges.Where(rc => property.Key.CanQuery(connection, rc.Value));

                IEnumerable<object> collectionSet = (IEnumerable<object>)db.GetType().GetProperty(property.Value).GetValue(db);

                foreach (CollectionSubscription cs in subscriptionGrouping)
                {
                    // ReSharper disable once PossibleMultipleEnumeration
                    IEnumerable<object> currentCollectionSet = collectionSet;

                    foreach (IPrefilter prefilter in cs.Prefilters)
                    {
                        currentCollectionSet = prefilter.Execute(currentCollectionSet);
                    }

                    List<object> currentCollectionSetLoaded = currentCollectionSet.ToList();
                    List<object[]> currentCollectionPrimaryValues = new List<object[]>();

                    // ReSharper disable once PossibleMultipleEnumeration
                    SendDataToClient(currentCollectionSetLoaded, currentCollectionPrimaryValues, property, db, cs, relevantChanges, connection);
                }
            }
        }

        private void SendDataToClient(List<object> currentCollectionSetLoaded, List<object[]> currentCollectionPrimaryValues,
            KeyValuePair<Type, string> property, RealtimeDbContext db, CollectionSubscription cs, IEnumerable<ChangeResponse> relevantChanges,
            WebsocketConnection connection)
        {
            foreach (object obj in currentCollectionSetLoaded)
            {
                // ReSharper disable once PossibleMultipleEnumeration
                SendRelevantFilesToClient(property, db, obj, currentCollectionPrimaryValues, cs, relevantChanges, connection);
            }

            foreach (object[] transmittedObject in cs.TransmittedData)
            {
                if (currentCollectionPrimaryValues.All(pks => pks.Except(transmittedObject).Any()))
                {
                    _ = connection.Send(new UnloadResponse
                    {
                        PrimaryValues = transmittedObject,
                        ReferenceId = cs.ReferenceId
                    });
                }
            }

            cs.TransmittedData = currentCollectionPrimaryValues;
        }

        private void SendRelevantFilesToClient(KeyValuePair<Type, string> property, RealtimeDbContext db, object obj,
            List<object[]> currentCollectionPrimaryValues, CollectionSubscription cs, IEnumerable<ChangeResponse> relevantChanges,
            WebsocketConnection connection)
        {
            object[] primaryValues = property.Key.GetPrimaryKeyValues(db, obj);
            currentCollectionPrimaryValues.Add(primaryValues);

            bool clientHasObject = cs.TransmittedData.Any(pks => !pks.Except(primaryValues).Any());

            if (clientHasObject)
            {
                ChangeResponse change = relevantChanges
                    .FirstOrDefault(c => !c.PrimaryValues.Except(primaryValues).Any());

                if (change != null)
                {
                    change.ReferenceId = cs.ReferenceId;
                    change.Value = change.Value.GetAuthenticatedQueryModel(connection);
                    _ = connection.Send(change);
                }
            }
            else
            {
                _ = connection.Send(new LoadResponse
                {
                    NewObject = obj.GetAuthenticatedQueryModel(connection),
                    ReferenceId = cs.ReferenceId
                });
            }
        }
    }
}
