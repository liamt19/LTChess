﻿namespace LTChess.Logic.Search
{
    public struct SearchInformation
    {
        /// <summary>
        /// The normal <see cref="Action"/> delegate doesn't allow passing by reference
        /// </summary>
        public delegate void ActionRef<T>(ref T info);

        /// <summary>
        /// The method to call (which must accept a <see langword="ref"/> <see cref="SearchInformation"/> parameter)
        /// during a search just before the depth increases.
        /// <br></br>
        /// By default, this will print out the "info depth # ..." string.
        /// </summary>
        public ActionRef<SearchInformation>? OnDepthFinish;

        /// <summary>
        /// The method to call (which must accept a <see langword="ref"/> <see cref="SearchInformation"/> parameter)
        /// when a search is finished.
        /// <br></br>
        /// By default, this will print "bestmove (move)".
        /// </summary>
        public ActionRef<SearchInformation>? OnSearchFinish;

        public Position Position;

        /// <summary>
        /// The depth to stop the search at.
        /// </summary>
        public int MaxDepth = DefaultSearchDepth;

        /// <summary>
        /// The number of nodes the search should stop at.
        /// </summary>
        public ulong MaxNodes = MaxSearchNodes;


        /// <summary>
        /// Set to true the first time that OnSearchFinish is invoked.
        /// </summary>
        public bool SearchFinishedCalled = false;

        /// <summary>
        /// Set to true while a search is ongoing, and false otherwise.
        /// </summary>
        public bool SearchActive = false;


        public TimeManager TimeManager;

        public bool IsInfinite => MaxDepth == Utilities.MaxDepth && this.TimeManager.MaxSearchTime == SearchConstants.MaxSearchTime;

        public SearchInformation(Position p) : this(p, SearchConstants.DefaultSearchDepth, SearchConstants.DefaultSearchTime)
        {
        }

        public SearchInformation(Position p, int depth) : this(p, depth, SearchConstants.DefaultSearchTime)
        {
        }

        public SearchInformation(Position p, int depth, int searchTime)
        {
            this.Position = p;
            this.MaxDepth = depth;

            this.TimeManager = new TimeManager();
            this.TimeManager.MaxSearchTime = searchTime;

            this.OnDepthFinish = PrintSearchInfo;
            this.OnSearchFinish = PrintBestMove;
        }

        public static SearchInformation Infinite(Position p)
        {
            SearchInformation si = new SearchInformation(p, Utilities.MaxDepth, SearchConstants.MaxSearchTime);
            si.MaxNodes = MaxSearchNodes;
            return si;
        }

        [MethodImpl(Inline)]
        public void SetMoveTime(int moveTime)
        {
            TimeManager.MaxSearchTime = moveTime;
            TimeManager.HasMoveTime = true;
        }

        /// <summary>
        /// Prints out the "info depth (number) ..." string
        /// </summary>
        [MethodImpl(Inline)]
        private void PrintSearchInfo(ref SearchInformation info)
        {
            Log(FormatSearchInformationMultiPV(ref info));

#if DEBUG
            SearchStatistics.TakeSnapshot(SearchPool.GetNodeCount(), (ulong)info.TimeManager.GetSearchTime());
#endif
        }

        /// <summary>
        /// Prints the best move from a search.
        /// </summary>
        [MethodImpl(Inline)]
        private void PrintBestMove(ref SearchInformation info)
        {
            Move bestThreadMove = SearchPool.GetBestThread().RootMoves[0].Move;
            Log("bestmove " + bestThreadMove.ToString());

            if (ServerGC)
            {
                //  Force a GC now if we are running in the server mode.
                ForceGC();
            }
        }

        public override string ToString()
        {
            return "MaxDepth: " + MaxDepth + ", " + "MaxNodes: " + MaxNodes + ", " + "MaxSearchTime: " + MaxSearchTime + ", "
                 + "SearchTime: " + (TimeManager == null ? "0 (NULL!)" : TimeManager.GetSearchTime());
        }
    }
}
