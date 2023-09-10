﻿
#define SHOW_STATS


using System.Runtime.InteropServices;

using LTChess.Logic.Core;
using LTChess.Logic.Data;
using LTChess.Logic.NN;
using LTChess.Logic.NN.HalfKA_HM;
using LTChess.Logic.NN.Simple768;
using LTChess.Logic.Search.Ordering;

using static LTChess.Logic.Search.Ordering.MoveOrdering;
using static LTChess.Logic.Search.SimpleQuiescence;

namespace LTChess.Logic.Search
{
    public static unsafe class SimpleSearch
    {
        /// <summary>
        /// The best move found in the previous call to Deepen.
        /// </summary>
        private static Move LastBestMove = Move.Null;

        /// <summary>
        /// The evaluation of that best move.
        /// </summary>
        private static int LastBestScore = ThreadedEvaluation.ScoreDraw;


        public static HistoryTable History;
        private static short* MainHistory;

        private static PrincipalVariationTable PvMoves = new PrincipalVariationTable();

        private static SearchStackEntry* _SearchStackBlock;

        static SimpleSearch()
        {
            _SearchStackBlock = (SearchStackEntry*) NativeMemory.AlignedAlloc((nuint)(sizeof(SearchStackEntry) * MaxPly), AllocAlignment);

            for (int i = 0; i < MaxPly; i++)
            {
                *(_SearchStackBlock + i) = new SearchStackEntry();
            }

            History = new HistoryTable();
        }

        /// <summary>
        /// Begin a new search with the parameters in <paramref name="info"/>.
        /// This performs iterative deepening, which searches at higher and higher depths as time goes on.
        /// <br></br>
        /// If <paramref name="allowDepthIncrease"/> is true, then the search will continue above the requested maximum depth
        /// so long as there is still search time remaining.
        /// </summary>
        [MethodImpl(Optimize)]
        public static void StartSearching(ref SearchInformation info, bool allowDepthIncrease = false)
        {
            TranspositionTable.TTUpdate();

            SearchStackEntry* ss = (_SearchStackBlock) + 10;
            for (int i = -10; i < MaxSearchStackPly; i++)
            {
                (ss + i)->Clear();
                (ss + i)->Ply = i;
            }

            PvMoves.Clear();
            NativeMemory.Clear(History.MainHistory, sizeof(short) * HistoryTable.MainHistoryElements);
            NativeMemory.Clear(History.CaptureHistory, sizeof(short) * HistoryTable.CaptureHistoryElements);

            LastBestMove = Move.Null;
            LastBestScore = ThreadedEvaluation.ScoreDraw;

            info.TimeManager.RestartTimer();

            int depth = 1;

            int maxDepthStart = info.MaxDepth;
            double maxTime = info.TimeManager.MaxSearchTime;

            info.NodeCount = 0;
            info.SearchActive = true;

            int alpha = AlphaStart;
            int beta = BetaStart;
            bool aspirationFailed = false;

            bool continueDeepening = true;
            while (continueDeepening)
            {
                info.MaxDepth = depth;

                if (UseAspirationWindows && !aspirationFailed && depth > 1)
                {
                    alpha = LastBestScore - (AspirationWindowMargin + (depth * AspirationMarginPerDepth));
                    beta = LastBestScore + (AspirationWindowMargin + (depth * AspirationMarginPerDepth));
#if DEBUG
                    //Log("Depth " + depth + " aspiration bounds are [A: " + alpha + ", eval: " + LastBestScore + ", B: " + beta + "]");
#endif
                }

                aspirationFailed = false;
                ulong prevNodes = info.NodeCount;

                int score = SimpleSearch.FindBest<RootNode>(ref info, ss, alpha, beta, info.MaxDepth, false);
                info.BestScore = score;

                ulong afterNodes = info.NodeCount;

                if (info.StopSearching)
                {
                    Log("Received StopSearching command just after Deepen at depth " + depth);
                    info.SearchActive = false;

                    //  If our search was interrupted, info.BestMove probably doesn't contain the actual best move,
                    //  and instead has the best move from whichever call to FindBest set it last.

                    if (PvMoves.Get(0) != LastBestMove)
                    {
                        Log("WARN PvMoves[0] " + PvMoves.Get(0).ToString(info.Position) + " != LastBestMove " + LastBestMove.ToString(info.Position));
                    }

                    info.SetLastMove(LastBestMove, LastBestScore);
                    info.OnSearchFinish?.Invoke(info);
                    info.TimeManager.ResetTimer();
                    return;
                }

                if (UseAspirationWindows && (info.BestScore <= alpha || info.BestScore >= beta))
                {
                    //  Redo the search with the default bounds, at the same depth.
                    alpha = AlphaStart;
                    beta = BetaStart;

                    //  TODO: not sure if engines are supposed to include nodes that they are searching again in this context.
                    info.NodeCount -= (afterNodes - prevNodes);

                    aspirationFailed = true;

#if (DEBUG || SHOW_STATS)
                    SearchStatistics.AspirationWindowFails++;
                    SearchStatistics.AspirationWindowTotalDepthFails += (ulong)depth;
#endif

#if DEBUG
                    //Log("Depth " + depth + " failed aspiration bounds, got " + info.BestScore);
                    if (SearchStatistics.AspirationWindowFails > 1000)
                    {
                        Log("Depth " + depth + " failed aspiration bounds " + SearchStatistics.AspirationWindowFails + " times, quitting");
                        return;
                    }
#endif
                    continue;
                }

                info.OnDepthFinish?.Invoke(info);


                if (continueDeepening && ThreadedEvaluation.IsScoreMate(info.BestScore, out int mateIn))
                {
                    Log(info.BestMove.ToString(info.Position) + " forces mate in " + mateIn + ", aborting at depth " + depth + " after " + info.TimeManager.GetSearchTime() + "ms");
                    break;
                }

                depth++;
                LastBestMove = info.BestMove;
                LastBestScore = info.BestScore;

                if (allowDepthIncrease && info.TimeManager.GetSearchTime() < SearchLowTimeThreshold && depth == maxDepthStart)
                {
                    maxDepthStart++;
                    //Log("Extended search depth to " + (maxDepthStart - 1));
                }

                continueDeepening = (depth <= maxDepthStart && depth < MaxDepth && info.TimeManager.GetSearchTime() <= maxTime);
            }

            info.OnSearchFinish?.Invoke(info);
            info.TimeManager.ResetTimer();

            if (UseSimple768)
            {
                NNUEEvaluation.ResetNN();
            }

            if (UseHalfKA)
            {
                HalfKA_HM.ResetNN();
            }
        }


        /// <summary>
        /// Finds the best move according to the Evaluation function, looking at least <paramref name="depth"/> moves in the future.
        /// </summary>
        /// <typeparam name="NodeType">One of <see cref="RootNode"/>, <see cref="PVNode"/>, or <see cref="NonPVNode"/></typeparam>
        /// <param name="info">Reference to the current search's SearchInformation</param>
        /// <param name="alpha">
        ///     The evaluation of the lower bound move. 
        ///     This will eventually be set equal to the evaluation of the best move we can make.
        /// </param>
        /// <param name="beta">
        ///     The evaluation of the upper bound move.
        ///     This essentially represents the best case scenerio for our opponent based on the moves we have available to us.
        ///     If we ever evaluate a position that scores higher than the beta, 
        ///     we immediately stop searching (return beta) because that would mean our opponent has a good reply
        ///     and we know that we can probably do better by making a different move.
        /// </param>
        /// <param name="depth">
        ///     The depth to search to.
        ///     When this reaches 0 we would ordinarily stop looking, 
        ///     but there is an additional quiescence search which looks at all available captures to make sure we didn't just 
        ///     make a blunder that could have been avoided by looking an additional move in the future.
        /// </param>
        /// <returns>The evaluation of the best move.</returns>
        [MethodImpl(Inline)]
        public static int FindBest<NodeType>(ref SearchInformation info, SearchStackEntry* ss, int alpha, int beta, int depth, bool cutNode) where NodeType : SearchNodeType
        {
            bool isRoot = (typeof(NodeType) == typeof(RootNode));
            bool isPV = (typeof(NodeType) != typeof(NonPVNode));

#if DEBUG || SHOW_STATS
            if (isRoot)
            {
                SearchStatistics.NM_Roots++;
            }
            if (isPV)
            {
                SearchStatistics.NM_PVs++;
            }
            if (typeof(NodeType) == typeof(NonPVNode))
            {
                SearchStatistics.NM_NonPVs++;
            }
#endif

            //  Check every few thousand nodes if we need to stop the search.
            if ((info.NodeCount & SearchCheckInCount) == 0)
            {
#if DEBUG || SHOW_STATS
                SearchStatistics.Checkups++;
#endif
                if (info.TimeManager.CheckUp(info.RootPlayerToMove))
                {
                    info.StopSearching = true;
                }
            }

            if (info.NodeCount >= info.MaxNodes || info.StopSearching)
            {
                info.StopSearching = true;
                return 0;
            }

#if DEBUG || SHOW_STATS
            SearchStatistics.NMCalls++;
#endif

            if (isPV)
            {
                PvMoves.InitializeLength(ss->Ply);
                if (ss->Ply > info.SelectiveDepth)
                {
                    info.SelectiveDepth = ss->Ply;
                }
            }

            //  At depth 0, we go into a Quiescence search, which verifies that the evaluation at this depth is reasonable
            //  by checking all of the available captures after the last move (in depth 1).
            if (depth <= 0)
            {
                return SimpleQuiescence.QSearch<NodeType>(ref info, (ss), alpha, beta, depth);
            }


            Position pos = info.Position;
            ref Bitboard bb = ref pos.bb;
            ulong posHash = pos.Hash;
            Move bestMove = Move.Null;

            ss->InCheck = pos.Checked;
            ss->TTHit = TranspositionTable.Probe(posHash, out TTEntry* tte);
            short ttScore = (ss->TTHit ? tte->Score : ScoreNone);
            CondensedMove ttMove = (ss->TTHit ? tte->BestMove : Move.Null);

            if (!isPV && tte->Depth >= depth && ttScore != ScoreNone)
            {
                if (!isPV && tte->Depth >= depth)
                {
                    //  We have already seen this position before at a higher depth,
                    //  so we can take the information from that depth and use it here.
                    if (tte->NodeType == TTNodeType.Exact)
                    {
#if DEBUG || SHOW_STATS
                        SearchStatistics.TTExactHits++;
#endif
                        return tte->Score;
                    }
                    else if (tte->NodeType == TTNodeType.Beta)
                    {
#if DEBUG || SHOW_STATS
                        SearchStatistics.TTBetaHits++;
#endif
                        alpha = Math.Max(alpha, tte->Score);
                    }
                    else if (tte->NodeType == TTNodeType.Alpha)
                    {
#if DEBUG || SHOW_STATS
                        SearchStatistics.TTAlphaHits++;
#endif
                        beta = Math.Min(beta, tte->Score);
                    }
                }
            }

            bool improving = false;

            int score = -ScoreMate - MaxPly;
            int bestScore = -ScoreMate - MaxPly;

            short eval;

            if (ss->InCheck)
            {
#if DEBUG || SHOW_STATS
                SearchStatistics.TT_InCheck_NM++;
#endif
                ss->StaticEval = eval = ScoreNone;
                improving = false;

                goto MoveLoop;
            }
            else if (ss->TTHit)
            {
#if DEBUG || SHOW_STATS
                SearchStatistics.TTHits_NM++;
#endif
                ss->StaticEval = eval = tte->StatEval;
                if (eval == ScoreNone)
                {
#if DEBUG || SHOW_STATS
                    SearchStatistics.TTHitNoScore_NM++;
#endif
                    ss->StaticEval = eval = info.GetEvaluation(pos);
                }
                else
                {
#if DEBUG || SHOW_STATS
                    SearchStatistics.TTHitGoodScore_NM++;
#endif
                }

                if (ttScore != ScoreNone)
                {
                    if ((ttScore >  ss->StaticEval) && tte->NodeType == TTNodeType.Alpha ||
                        (ttScore <= ss->StaticEval) && tte->NodeType == TTNodeType.Beta)
                    {
#if DEBUG || SHOW_STATS
                        SearchStatistics.TTScoreFit_NM++;
#endif
                        eval = ttScore;
                    }
                }
            }
            else
            {
#if DEBUG || SHOW_STATS
                SearchStatistics.TTMisses_NM++;
#endif
                ss->StaticEval = eval = info.GetEvaluation(pos);
                tte->Update(posHash, ScoreNone, TTNodeType.Invalid, TTEntry.DepthNone, Move.Null, eval);
            }

            if (UseReverseFutilityPruning && CanReverseFutilityPrune(ss->InCheck, isPV, beta, depth))
            {
                var lastScore = (ss - 2)->StaticEval;
                if (lastScore == ScoreNone)
                {
                    lastScore = (ss - 4)->StaticEval;
                }
                if (lastScore == ScoreNone)
                {
                    //  https://github.com/official-stockfish/Stockfish/blob/af110e02ec96cdb46cf84c68252a1da15a902395/src/search.cpp#L754
                    lastScore = 173;
                }

                var improvement = ss->StaticEval - lastScore;
                improving = (ss->Ply >= 2 && improvement > 0);

                if (eval - GetReverseFutilityMargin(depth, improving) >= beta && eval >= beta)
                {
#if DEBUG || SHOW_STATS
                    SearchStatistics.ReverseFutilityPrunedNodes++;
#endif
                    return beta;
                }
            }

            if (UseRazoring && CanRazoringPrune(ss->InCheck, isPV, eval, alpha, depth))
            {
                score = SimpleQuiescence.QSearch<NodeType>(ref info, (ss), alpha, beta, depth);
                if (score <= alpha)
                {
#if DEBUG || SHOW_STATS
                    SearchStatistics.RazoredNodes++;
#endif
                    return score;
                }
            }


            //if (CanNullMovePrune(pos, ss->InCheck, isPV, eval, beta, depth))
            if (depth >= 3 && !isPV && eval >= beta && ss->StaticEval >= beta && pos.MaterialCountNonPawn[pos.ToMove] > 0 && (ss - 1)->CurrentMove != Move.Null)
            {
                int reduction = SearchConstants.NullMovePruningMinDepth + (depth / SearchConstants.NullMovePruningMinDepth);

                ss->CurrentMove = Move.Null;

                info.Position.MakeNullMove();
                int nullMoveEval = -FindBest<NonPVNode>(ref info, (ss + 1), -beta, -beta + 1, depth - reduction, !cutNode);
                info.Position.UnmakeNullMove();

                if (nullMoveEval >= beta)
                {
                    //  Then our opponent couldn't improve their position sufficiently with a free move,
#if DEBUG || SHOW_STATS
                    SearchStatistics.NullMovePrunedNodes++;
#endif
                    return beta;
                }
            }


            if (!isRoot && isPV && ttMove.IsNull())
            {
                depth -= 2 + 2 * (ss->TTHit && tte->Depth >= depth ? 1 : 0);
                if (depth <= 0)
                {
                    return SimpleQuiescence.QSearch<NodeType>(ref info, (ss), alpha, beta, depth);
                }
            }


            MoveLoop:

            Span<Move> captureMoves = stackalloc Move[32];
            Span<Move> quietMoves = stackalloc Move[64];

            Span<Move> list = stackalloc Move[NormalListCapacity];
            int size = pos.GenAllPseudoLegalMovesTogether(list);

            Span<int> scores = stackalloc int[size];
            AssignNormalMoveScores(pos, History, list, scores, ss, size, ss->Ply, tte->BestMove);

            int playedMoves = 0;
            int legalMoves = 0;

            int lmpMoves = 0;

            int quietCount = 0;
            int captureCount = 0;

            int lmpCutoff = LMPTable[improving ? 1 : 0][depth];

            for (int i = 0; i < size; i++)
            {
                OrderNextMove(list, scores, size, i);

                Move m = list[i];

                if (!pos.IsLegal(m))
                {
                    continue;
                }

                legalMoves++;

                if (!m.Capture)
                {
                    lmpMoves++;
                    
                    if (UseFutilityPruning && CanFutilityPrune(ss->InCheck, isPV, alpha, beta, depth))
                    {
                        if (eval + (SearchConstants.FutilityPruningMarginPerDepth * depth) < alpha)
                        {
                            if (i == 0)
                            {
                                //  In the off chance that the move ordered first would fail futility pruning,
                                //  we should at least check the next node to see if it will too.
                                continue;
                            }
                            else
                            {
#if DEBUG || SHOW_STATS
                                SearchStatistics.FutilityPrunedNoncaptures++;
                                SearchStatistics.FutilityPrunedMoves += (ulong)(size - i);
#endif
                                break;
                            }
                        }
                    }

                    //  Only prune if our alpha has changed, meaning we have found at least 1 good move.
                    if (UseLateMovePruning && CanLateMovePrune(ss->InCheck, isPV, isRoot, depth) && (alpha != AlphaStart) && (lmpMoves >= lmpCutoff))
                    {
#if DEBUG || SHOW_STATS
                        SearchStatistics.LateMovePrunings++;
                        SearchStatistics.LateMovePrunedMoves += (ulong)(size - i);
#endif
                        break;
                    }

                }

                playedMoves++;

                prefetch(Unsafe.AsPointer(ref TranspositionTable.GetCluster(pos.HashAfter(m))));
                ss->CurrentMove = m;

                pos.MakeMove(m);

                info.NodeCount++;
#if DEBUG || SHOW_STATS
                SearchStatistics.NMNodes++;
#endif


                if ((info.Position.IsThreefoldRepetition() || info.Position.IsInsufficientMaterial()))
                {
                    //  Instead of looking further and probably breaking something,
                    //  Just evaluate this move as a draw here and keep looking at the others.
                    score = -ThreadedEvaluation.ScoreDraw;
                }
                else if (i == 0)
                {
                    //  This is the move ordered first, so we treat this as our PV and search
                    //  without any reductions.
                    score = -SimpleSearch.FindBest<PVNode>(ref info, (ss + 1), -beta, -alpha, depth - 1, cutNode);
                }
                else
                {

                    bool doFullSearch = false;

                    if (depth >= 3 && legalMoves >= 2 && !(isPV && m.Capture))
                    {

                        int R = LogarithmicReductionTable[depth][i];
#if DEBUG || SHOW_STATS
                        SearchStatistics.LMRReductions++;
                        SearchStatistics.LMRReductionTotal += (ulong)R;
#endif

                        if ((bb.GetPieceAtIndex(m.From) == Piece.King && ss->InCheck))
                        {
#if DEBUG || SHOW_STATS
                            SearchStatistics.ExtensionsKingChecked++;
#endif

                            R--;
                        }

                        if (m.Checks && depth >= 8)
                        {
#if DEBUG || SHOW_STATS
                            SearchStatistics.ExtensionsCausesCheck++;
#endif
                            R--;
                        }

                        if (!improving)
                        {
#if DEBUG || SHOW_STATS
                            SearchStatistics.ReductionsNotImproving++;
#endif
                            R++;
                        }

                        if (isPV)
                        {
#if DEBUG || SHOW_STATS
                            SearchStatistics.ExtensionsPV++;
#endif
                            R--;
                        }

                        if (m.Equals(ttMove))
                        {
#if DEBUG || SHOW_STATS
                            SearchStatistics.ExtensionsTTMove++;
#endif
                            R--;
                        }

                        R = Math.Min(depth - 1, Math.Max(R, 1));
                        score = -SimpleSearch.FindBest<NonPVNode>(ref info, (ss + 1), -alpha - 1, -alpha, depth - R, cutNode);
                        doFullSearch = score > alpha && R > 1;
                    }
                    else
                    {
                        doFullSearch = !isPV || playedMoves > 1;
                    }

                    if (doFullSearch)
                    {
                        score = -SimpleSearch.FindBest<NonPVNode>(ref info, (ss + 1), -alpha - 1, -alpha, depth - 1, cutNode);
                    }

                    if (isPV && (playedMoves == 1 || (score > alpha && (isRoot || score < beta))))
                    {
                        score = -SimpleSearch.FindBest<PVNode>(ref info, (ss + 1), -beta, -alpha, depth - 1, cutNode);
                    }

                    
                }


                pos.UnmakeMove(m);

                if (score > bestScore)
                {
                    bestScore = score;

                    if (score > alpha)
                    {
                        //  This is the best move so far
                        bestMove = m;

                        if (isPV)
                        {
                            PvMoves.Insert(ss->Ply, m);
                        }

                        if (score >= beta)
                        {

#if DEBUG || SHOW_STATS
                            SearchStatistics.BetaCutoffs++;
#endif

                            if (!m.IsNull() && !m.Capture)
                            {
                                if (ss->Killer0 != m)
                                {
#if DEBUG || SHOW_STATS
                                    SearchStatistics.KillerMovesAdded++;
#endif
                                    ss->Killer1 = ss->Killer0;
                                    ss->Killer0 = m;
                                }
                            }

                            break;

                        }
                        else
                        {
                            alpha = score;
                        }
                    }
                }
            
                if (m != bestMove)
                {
                    if (m.Capture && captureCount < 32)
                    {
                        captureMoves[captureCount++] = m;
                    }
                    else if (!m.Capture && quietCount < 64)
                    {
                        quietMoves[quietCount++] = m;
                    }
                }
            }

            if (legalMoves == 0)
            {
                if (pos.CheckInfo.InCheck || pos.CheckInfo.InDoubleCheck)
                {
                    //return info.MakeMateScore();
                    return -ScoreMate + ss->Ply;
                }
                else
                {
                    return -ThreadedEvaluation.ScoreDraw;
                }
            }

            if (bestMove != Move.Null)
            {
                UpdateStats(pos, ss, bestMove, bestScore, beta, depth, quietMoves, quietCount, captureMoves, captureCount);
            }
            
            if (bestScore <= alpha)
            {
                ss->TTPV = ss->TTPV || ((ss - 1)->TTPV && depth > 3);
            }


            TTNodeType nodeTypeToSave;
            if (bestScore >= beta)
            {
                nodeTypeToSave = TTNodeType.Alpha;
            }
            else if (isPV && !bestMove.IsNull())
            {
                nodeTypeToSave = TTNodeType.Exact;
            }
            else
            {
                nodeTypeToSave = TTNodeType.Beta;
            }
            
            tte->Update(posHash, (short)bestScore, nodeTypeToSave, depth, bestMove, ss->StaticEval);
            info.BestMove = bestMove;
            info.BestScore = bestScore;

#if DEBUG || SHOW_STATS
            SearchStatistics.NMCompletes++;
#endif

            return bestScore;
        }



        private static void UpdateStats(Position pos, SearchStackEntry* ss, Move bestMove, int bestScore, int beta, int depth,
                                        Span<Move> quietMoves, int quietCount, Span<Move> captureMoves, int captureCount)
        {

            int thisPiece = pos.bb.GetPieceAtIndex(bestMove.From);
            int capturedPiece = pos.bb.GetPieceAtIndex(bestMove.To);

            int quietMoveBonus = StatBonus(depth + 1);

            if (bestMove.Capture)
            {
                //int idx = ((thisPiece + (PieceNB * pos.ToMove)) + (PieceNB * ColorNB) * (capturedPiece + (SquareNB * bestMove.To)));
                int idx = HistoryTable.CapIndex(thisPiece, pos.ToMove, bestMove.To, capturedPiece);
                History.ApplyBonus(History.CaptureHistory, idx, quietMoveBonus, HistoryTable.CaptureClamp);
            }
            else
            {

                int captureBonus = (bestScore > beta + 150) ? quietMoveBonus : StatBonus(depth);

                if (ss->Killer0 != bestMove)
                {
#if DEBUG || SHOW_STATS
                    SearchStatistics.KillerMovesAdded++;
#endif
                    ss->Killer1 = ss->Killer0;
                    ss->Killer0 = bestMove;
                }

                History.ApplyBonus(History.MainHistory, ((pos.ToMove * HistoryTable.MainHistoryPCStride) + bestMove.MoveMask), captureBonus, HistoryTable.MainHistoryClamp);
                
                for (int i = 0; i < quietCount; i++)
                {
                    History.ApplyBonus(History.MainHistory, ((pos.ToMove * HistoryTable.MainHistoryPCStride) + quietMoves[i].MoveMask), -captureBonus, HistoryTable.MainHistoryClamp);
                }
            }

            for (int i = 0; i < captureCount; i++)
            {
                //int idx = ((pos.bb.GetPieceAtIndex(captureMoves[i].From) + (PieceNB * pos.ToMove)) + (PieceNB * ColorNB) * (pos.bb.GetPieceAtIndex(captureMoves[i].To) +  (SquareNB * captureMoves[i].To)));
                int idx = HistoryTable.CapIndex(pos.bb.GetPieceAtIndex(captureMoves[i].From), pos.ToMove, captureMoves[i].To, pos.bb.GetPieceAtIndex(captureMoves[i].To));
                History.ApplyBonus(History.CaptureHistory, idx, -quietMoveBonus, HistoryTable.CaptureClamp);
            }
        }


        [MethodImpl(Inline)]
        private static int StatBonus(int depth)
        {
            return Math.Min(350 * (depth + 1) - 550, 1550);
        }


        [MethodImpl(Inline)]
        public static bool CanLateMovePrune(bool isInCheck, bool isPV, bool isRoot, int depth)
        {
            if (!isInCheck && !isPV && !isRoot && depth <= SearchConstants.LMPDepth)
            {
                return true;
            }

            return false;
        }


        [MethodImpl(Inline)]
        public static bool CanRazoringPrune(bool isInCheck, bool isPV, int staticEval, int alpha, int depth)
        {
            if (!isInCheck && !isPV && depth <= 4)
            {
                if (staticEval + (SearchConstants.RazoringMargin * depth) <= alpha)
                {
                    return true;
                }
            }

            return false;
        }



        [MethodImpl(Inline)]
        public static bool CanFutilityPrune(bool isInCheck, bool isPV, int alpha, int beta, int depth)
        {
            if (!isInCheck && !isPV && depth <= SearchConstants.FutilityPruningMaxDepth)
            {
                if (!ThreadedEvaluation.IsScoreMate(alpha, out _) && !ThreadedEvaluation.IsScoreMate(beta, out _))
                {
                    return true;
                }
            }

            return false;
        }


        [MethodImpl(Inline)]
        public static bool CanReverseFutilityPrune(bool isInCheck, bool isPV, int beta, int depth)
        {
            if (!isInCheck && !isPV && depth <= SearchConstants.ReverseFutilityPruningMaxDepth && !ThreadedEvaluation.IsScoreMate(beta, out _))
            {
                return true;
            }

            return false;
        }

        [MethodImpl(Inline)]
        public static int GetReverseFutilityMargin(int depth, bool improving)
        {
            return (depth * SearchConstants.ReverseFutilityPruningPerDepth) - ((improving ? 1 : 0) * SearchConstants.ReverseFutilityPruningImproving);
        }


        [MethodImpl(Inline)]
        public static bool CanNullMovePrune(Position p, bool isInCheck, bool isPV, int staticEval, int beta, int depth)
        {
            if (!isInCheck && !isPV && depth >= SearchConstants.NullMovePruningMinDepth && staticEval >= beta)
            {
                //  Shouldn't be used in endgames.
                int weakerSideMaterial = Math.Min(p.MaterialCount[Color.White], p.MaterialCount[Color.Black]);
                return (weakerSideMaterial > EvaluationConstants.EndgameMaterial);
            }

            return false;
        }




        [MethodImpl(Inline)]
        public static bool CanLateMoveReduce(ref SearchInformation info, Move m, int depth)
        {
            if (info.Position.CheckInfo.InCheck || info.Position.CheckInfo.InDoubleCheck || m.CausesCheck || m.CausesDoubleCheck)
            {
                return false;
            }

            if (m.Capture || m.Promotion || depth < SearchConstants.LMRDepth)
            {
                return false;
            }

            if (info.Position.bb.IsPasser(m.From))
            {
                return false;
            }

            return true;
        }

        [MethodImpl(Inline)]
        public static int GetLateMoveReductionAmount(int listLen, int listIndex, int depth)
        {
            // Always reduce by 1, and reduce by 1 again if this move is ordered late in the list.
            bool isLateInList = (listIndex * 2 > listLen);

            bool isVeryLateInList = (listIndex * 4 > listLen * 3);

            if (isVeryLateInList)
            {
                // Reduce by slightly more if the move is very close to the end of the list.
                return SearchConstants.LMRReductionAmount + (depth / 2);
            }
            else if (isLateInList)
            {
                return SearchConstants.LMRReductionAmount + (depth / 4);
            }

            return SearchConstants.LMRReductionAmount;
        }


        /// <summary>
        /// Returns the PV line from a search, which is the series of moves that the engine thinks will be played.
        /// </summary>
        [MethodImpl(Inline)]
        public static int GetPV(in Move[] moves)
        {
            int max = PvMoves.Count();

            if (max == 0)
            {
                Log("WARN PvMoves.Count was 0, trying to get line[0] anyways");
                int i = 0;
                while (i < PrincipalVariationTable.TableSize)
                {
                    moves[i] = PvMoves.Get(i);
                    if (moves[i].IsNull())
                    {
                        break;
                    }
                    i++;
                }
                max = i;
            }

            for (int i = 0; i < PvMoves.Count(); i++)
            {
                moves[i] = PvMoves.Get(i);
            }

            return max;
        }

    }
}
