using System.Collections;
using System.Collections.Generic;
using Mirror.Tests.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mirror.TransformSyncing.Tests.Runtime
{
    public class NetworkTransformSnapshotInterpolationTest : HostSetup
    {
        readonly List<GameObject> spawned = new List<GameObject>();

        private NetworkTransformSnapshotInterpolation serverNT;
        private NetworkTransformSnapshotInterpolation clientNT;

        protected override bool AutoAddPlayer => false;

        protected override void afterStartHost()
        {
            GameObject serverGO = new GameObject("server object");
            GameObject clientGO = new GameObject("client object");
            spawned.Add(serverGO);
            spawned.Add(clientGO);

            NetworkIdentity serverNI = serverGO.AddComponent<NetworkIdentity>();
            NetworkIdentity clientNI = clientGO.AddComponent<NetworkIdentity>();

            serverNT = serverGO.AddComponent<NetworkTransformSnapshotInterpolation>();
            clientNT = clientGO.AddComponent<NetworkTransformSnapshotInterpolation>();

            // set up Identitys so that server object can send message to client object in host mode
            serverNI.OnStartServer();
            serverNI.RebuildObservers(true);

            clientNI.netId = serverNI.netId;
            NetworkIdentity.spawned[serverNI.netId] = clientNI;
            clientNI.OnStartClient();

            // reset both transforms
            serverGO.transform.position = Vector3.zero;
            clientGO.transform.position = Vector3.zero;
        }

        protected override void beforeStopHost()
        {
            foreach (GameObject obj in spawned)
            {
                Object.Destroy(obj);
            }
        }

        [UnityTest]
        public IEnumerator SyncPositionFromServerToClient()
        {
            Vector3[] positions = new Vector3[] {
                new Vector3(1, 2, 3),
                new Vector3(2, 2, 3),
                new Vector3(2, 3, 5),
                new Vector3(2, 3, 5),
            };

            foreach (Vector3 position in positions)
            {
                serverNT.transform.position = position;
                // wait more than needed to check end position is reached
                yield return new WaitForSeconds(0.5f);

                Assert.That(clientNT.transform.position, Is.EqualTo(position));
            }
        }
    }
}
