﻿using System.Runtime.InteropServices;

namespace LTChess.Logic.Transposition
{
    public static unsafe class TranspositionTable
    {

        public const int TT_BOUND_MASK = (0x3             );
        public const int TT_PV_MASK    = (0x4             );
        public const int TT_AGE_MASK   = (0xF8            );
        public const int TT_AGE_INC    = (0x8             );
        public const int TT_AGE_CYCLE  = (255 + TT_AGE_INC);

        private const int MinTTClusters = 1000;

        public const int EntriesPerCluster = 3;
        private static TTCluster* Clusters;
        private static ulong ClusterCount;
        public static ushort Age = 0;

        private static bool Initialized = false;

        static TranspositionTable()
        {
            if (!Initialized)
            {
                Initialize();

                if (EnableAssertions)
                {
                    Assert(ClusterCount > MinTTClusters,
                        "The Transposition Table is undersized! It must have space for at least " + MinTTClusters + " clusters, " +
                        "but it currently can only fit " + ClusterCount);
                }
            }
        }

        /// <summary>
        /// Allocates <paramref name="mb"/> megabytes of memory for the Transposition Table, and zeroes out each entry.
        /// <para></para>
        /// 1 mb fits 32,768 clusters, which is 98,304 TTEntry's.
        /// </summary>
        public static unsafe void Initialize(int mb = 32)
        {

            ClusterCount = ((ulong)mb * 0x100000UL) / (ulong)sizeof(TTCluster);
            Clusters = (TTCluster*) AlignedAllocZeroed((nuint)(sizeof(TTCluster) * (int)ClusterCount), AllocAlignment);
            for (ulong i = 0; i < ClusterCount; i++)
            {
                Clusters[i] = new TTCluster();
            }
        }

        /// <summary>
        /// Reinitializes each <see cref="TTCluster"/> within the table.
        /// </summary>
        [MethodImpl(Inline)]
        public static void Clear()
        {
            SearchPool.MainThread.WaitForThreadFinished();

            for (ulong i = 0; i < ClusterCount; i++)
            {
                TTEntry* cluster = (TTEntry*) &Clusters[i];

                if (cluster[0].Key != 0 || cluster[1].Key != 0 || cluster[2].Key != 0)
                {
                    Clusters[i].Clear();
                }
            }
        }

        /// <summary>
        /// Returns a pointer to the <see cref="TTCluster"/> that the <paramref name="hash"/> maps to.
        /// </summary>
        [MethodImpl(Inline)]
        public static TTCluster* GetCluster(ulong hash)
        {
            return (Clusters + ((ulong)(((UInt128)hash * (UInt128)ClusterCount) >> 64)));
        }


        /// <summary>
        /// Sets <paramref name="tte"/> to the address of a <see cref="TTEntry"/>.
        /// <para></para>
        /// If the <see cref="TTCluster"/> that the <paramref name="hash"/> maps to contains an entry, 
        /// then <paramref name="tte"/> is the address of that entry and this method returns true, signifying that this was a TT Hit.
        /// <br></br>
        /// Otherwise, this method sets <paramref name="tte"/> to the address of the <see cref="TTEntry"/> within the cluster that should
        /// be overwritten with new information, and this method returns false.
        /// </summary>
        public static bool Probe(ulong hash, out TTEntry* tte)
        {
            TTCluster* cluster = GetCluster(hash);
            tte = (TTEntry*) cluster;

            var key = (ushort) hash;

            for (int i = 0; i < EntriesPerCluster; i++)
            {
                //  If the entry's key matches, or the entry is empty, then pick this one.
                if (tte[i].Key == key || tte[i].IsEmpty)
                {
                    tte[i].AgePVType = (sbyte)(Age | (tte[i].AgePVType & (TT_AGE_INC - 1)));

                    tte = &tte[i];

                    //  We return true if the entry isn't empty, which means that tte is valid.
                    //  Check tte[0] here, not tte[i].
                    return !tte[0].IsEmpty;
                }
            }

            //  We didn't find an entry for this hash, so instead we will choose one of the 
            //  non-working entries in this cluster to possibly be overwritten / updated, and return false.

            //  Replace the first entry, unless the 2nd or 3rd is a better option.
            TTEntry* replace = tte;
            for (int i = 1; i < EntriesPerCluster; i++)
            {
                if ((replace->Depth - (TT_AGE_CYCLE + Age - replace->AgePVType) & TT_AGE_MASK) >
                    (  tte[i].Depth - (TT_AGE_CYCLE + Age -   tte[i].AgePVType) & TT_AGE_MASK))
                {
                    replace = &tte[i];
                }
            }

            tte = replace;
            return false;
        }




        /// <summary>
        /// Increases the age that TT entries must have to be considered a "TT Hit".
        /// <br></br>
        /// This is done on every call to <see cref="Threads.SearchThread.MainThreadSearch"/> to prevent the transposition table
        /// from spilling into another search.
        /// </summary>
        [MethodImpl(Inline)]
        public static void TTUpdate()
        {
            Age += TT_AGE_INC;
        }



        [MethodImpl(Inline)]
        public static int GetHashFull()
        {
            int entries = 0;

            for (int i = 0; i < MinTTClusters; i++)
            {
                TTEntry* cluster = (TTEntry*) &Clusters[i];

                for (int j = 0; j < EntriesPerCluster; j++)
                {
                    if ((cluster[j].AgePVType & TT_AGE_MASK) == Age)
                    {
                        entries++;
                    }
                }
            }
            return entries / EntriesPerCluster;
        }


        public static void PrintClusterStatus()
        {
            int recentEntries = 0;
            int Beta = 0;
            int Alpha = 0;
            int Exact = 0;
            int Invalid = 0;

            int NullMoves = 0;

            int[] slots = new int[3];

            for (ulong i = 0; i < ClusterCount; i++)
            {
                TTEntry* cluster = (TTEntry*)&Clusters[i];
                for (int j = 0; j < EntriesPerCluster; j++)
                {
                    var tt = cluster[j];
                    if (tt.NodeType == TTNodeType.Beta)
                    {
                        Beta++;
                    }
                    else if (tt.NodeType == TTNodeType.Alpha)
                    {
                        Alpha++;
                    }
                    else if (tt.NodeType == TTNodeType.Exact)
                    {
                        Exact++;
                    }
                    else
                    {
                        Invalid++;
                    }

                    if (tt.NodeType != TTNodeType.Invalid)
                    {
                        slots[j]++;
                    }

                    if (tt.BestMove.IsNull() && tt.NodeType != TTNodeType.Invalid)
                    {
                        NullMoves++;
                    }

                    if (tt.Age == Age)
                    {
                        recentEntries++;
                    }
                }
            }

            int entries = Beta + Alpha + Exact;

            //  "Full" is the total number of entries of any age in the TT.
            Log("Full:\t " + entries + " / " + (ClusterCount * EntriesPerCluster) + " = " + (((double)entries / (ClusterCount * EntriesPerCluster)) * 100) + "%");

            //  "Recent" is the number of entries that have the same age as the TT's Age.
            Log("Recent:\t " + recentEntries + " / " + (ClusterCount * EntriesPerCluster) + " = " + (((double)recentEntries / (ClusterCount * EntriesPerCluster)) * 100) + "%");
            
            //  "Slots[0,1,2]" are the number of entries that exist in each TTCluster slot
            Log("Slots:\t " + slots[0] + " / " + slots[1] + " / " + slots[2]);
            Log("Alpha:\t " + Alpha);
            Log("Beta:\t " + Beta);
            Log("Exact:\t " + Exact);
            Log("Invalid: " + Invalid);
            Log("Null:\t " + NullMoves);
        }
    }
}
