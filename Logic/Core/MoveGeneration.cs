﻿namespace Lizard.Logic.Core
{
    public unsafe partial class Position
    {
        /// Almost everything in this file is based heavily on the move generation of Stockfish.


        /// <summary>
        /// Generates the pseudo-legal moves for all of the pawns in the position, placing them into the 
        /// ScoredMove <paramref name="list"/> starting at the index <paramref name="size"/> and the new number
        /// of moves in the list is returned.
        /// <para></para>
        /// Only moves which have a To square whose bit is set in <paramref name="targets"/> will be generated.
        /// <br></br>
        /// For example:
        /// <br></br>
        /// When generating captures, <paramref name="targets"/> should be set to our opponent's color mask.
        /// <br></br>
        /// When generating evasions, <paramref name="targets"/> should be set to the <see cref="LineBB"/> between our king and the checker, which is the mask
        /// of squares that would block the check or capture the piece giving check.
        /// </summary>
        public int GenPawns<GenType>(ScoredMove* list, ulong targets, int size) where GenType : MoveGenerationType
        {
            bool loudMoves = typeof(GenType) == typeof(GenLoud);
            bool quiets = typeof(GenType) == typeof(GenQuiets);
            bool quietChecks = typeof(GenType) == typeof(GenQChecks);
            bool evasions = typeof(GenType) == typeof(GenEvasions);
            bool nonEvasions = typeof(GenType) == typeof(GenNonEvasions);


            ulong rank7 = (ToMove == White) ? Rank7BB : Rank2BB;
            ulong rank3 = (ToMove == White) ? Rank3BB : Rank6BB;

            int up = ShiftUpDir(ToMove);

            int theirColor = Not(ToMove);

            ulong us = bb.Colors[ToMove];
            ulong them = bb.Colors[theirColor];
            ulong captureSquares = evasions ? State->Checkers : them;

            ulong occupiedSquares = them | us;
            ulong emptySquares = ~occupiedSquares;

            ulong ourPawns = us & bb.Pieces[Piece.Pawn];
            ulong promotingPawns = ourPawns & rank7;
            ulong notPromotingPawns = ourPawns & ~rank7;

            int theirKing = State->KingSquares[theirColor];

            if (!loudMoves)
            {
                //  Include pawn pushes
                ulong moves = Forward(ToMove, notPromotingPawns) & emptySquares;
                ulong twoMoves = Forward(ToMove, moves & rank3) & emptySquares;

                if (evasions)
                {
                    //  Only include pushes which block the check
                    moves &= targets;
                    twoMoves &= targets;
                }

                if (quietChecks)
                {
                    //  Only include pushes that cause a discovered check, or directly check the king
                    ulong discoveryPawns = State->BlockingPieces[theirColor] & ~GetFileBB(theirKing);
                    moves &= PawnAttackMasks[theirColor][theirKing] | Shift(up, discoveryPawns);
                    twoMoves &= PawnAttackMasks[theirColor][theirKing] | Shift(up + up, discoveryPawns);
                }

                while (moves != 0)
                {
                    int to = poplsb(&moves);

                    ref Move m = ref list[size++].Move;
                    m.SetNew(to - up, to);
                }

                while (twoMoves != 0)
                {
                    int to = poplsb(&twoMoves);

                    ref Move m = ref list[size++].Move;
                    m.SetNew(to - up - up, to);
                }
            }

            if (promotingPawns != 0)
            {
                ulong promotions = Shift(up, promotingPawns) & emptySquares;
                ulong promotionCapturesL = Shift(up + Direction.WEST, promotingPawns) & captureSquares;
                ulong promotionCapturesR = Shift(up + Direction.EAST, promotingPawns) & captureSquares;

                if (evasions)
                {
                    //  Only promote on squares that block the check or capture the checker.
                    promotions &= targets;
                }

                while (promotions != 0)
                {
                    int to = poplsb(&promotions);
                    size = NewMakePromotionChecks(list, to - up, to, false, size);
                }

                while (promotionCapturesL != 0)
                {
                    int to = poplsb(&promotionCapturesL);
                    size = NewMakePromotionChecks(list, to - up - Direction.WEST, to, true, size);
                }

                while (promotionCapturesR != 0)
                {
                    int to = poplsb(&promotionCapturesR);
                    size = NewMakePromotionChecks(list, to - up - Direction.EAST, to, true, size);
                }
            }

            if (!(quiets || quietChecks))
            {
                //  Don't generate captures for quiets
                ulong capturesL = Shift(up + Direction.WEST, notPromotingPawns) & captureSquares;
                ulong capturesR = Shift(up + Direction.EAST, notPromotingPawns) & captureSquares;

                while (capturesL != 0)
                {
                    int to = poplsb(&capturesL);

                    ref Move m = ref list[size++].Move;
                    m.SetNew(to - up - Direction.WEST, to, capture: true);

                }

                while (capturesR != 0)
                {
                    int to = poplsb(&capturesR);

                    ref Move m = ref list[size++].Move;
                    m.SetNew(to - up - Direction.EAST, to, capture: true);

                }

                if (State->EPSquare != EPNone)
                {
                    if (evasions && (targets & (SquareBB[State->EPSquare + up])) != 0)
                    {
                        //  When in check, we can only en passant if the pawn being captured is the one giving check
                        return size;
                    }

                    ulong mask = notPromotingPawns & PawnAttackMasks[theirColor][State->EPSquare];
                    while (mask != 0)
                    {
                        int from = poplsb(&mask);

                        ref Move m = ref list[size++].Move;
                        m.SetNew(from, State->EPSquare);
                        m.EnPassant = true;
                    }
                }
            }

            return size;


            int NewMakePromotionChecks(ScoredMove* list, int from, int promotionSquare, bool isCapture, int size)
            {
                int highPiece = Knight;
                int lowPiece = Queen;

                if (quiets && isCapture)
                {
                    return size;
                }

                if (evasions || nonEvasions)
                {
                    //  All promotions are valid
                    highPiece = Queen;
                    lowPiece = Knight;
                }

                if (loudMoves)
                {
                    //  GenLoud makes all promotions for captures, and only makes Queens for non-capture promotions
                    highPiece = Queen;
                    lowPiece = isCapture ? Knight : Queen;
                }

                if (quiets)
                {
                    //  GenQuiets only makes underpromotions
                    lowPiece = Knight;
                    highPiece = Rook;
                }

                //  GenQChecks makes nothing.

                for (int promotionPiece = lowPiece; promotionPiece <= highPiece; promotionPiece++)
                {
                    ref Move m = ref list[size++].Move;
                    m.SetNew(from, promotionSquare, promotionPiece);

                    if ((them & SquareBB[promotionSquare]) != 0)
                    {
                        m.Capture = true;
                    }
                }

                return size;
            }
        }


        /// <summary>
        /// Generates all the pseudo-legal moves for the player whose turn it is to move, given the <see cref="MoveGenerationType"/>.
        /// These are placed in the ScoredMove <paramref name="list"/> starting at the index <paramref name="size"/> and the new number
        /// of moves in the list is returned.
        /// </summary>
        public int GenAll<GenType>(ScoredMove* list, int size = 0) where GenType : MoveGenerationType
        {
            bool loudMoves = typeof(GenType) == typeof(GenLoud);
            bool quiets = typeof(GenType) == typeof(GenQuiets);
            bool evasions = typeof(GenType) == typeof(GenEvasions);
            bool quietChecks = typeof(GenType) == typeof(GenQChecks);
            bool nonEvasions = typeof(GenType) == typeof(GenNonEvasions);

            ulong us = bb.Colors[ToMove];
            ulong them = bb.Colors[Not(ToMove)];
            ulong occ = us | them;

            int ourKing = State->KingSquares[ToMove];
            int theirKing = State->KingSquares[Not(ToMove)];

            ulong targets = 0;

            // If we are generating evasions and in double check, then skip non-king moves.
            if (!(evasions && MoreThanOne(State->Checkers)))
            {
                targets = evasions ? LineBB[ourKing][lsb(State->Checkers)]
                        : nonEvasions ? ~us
                        : loudMoves ? them
                        : ~occ;

                size = GenPawns<GenType>(list, targets, size);
                size = GenNormal(list, Knight, quietChecks, targets, size);
                size = GenNormal(list, Bishop, quietChecks, targets, size);
                size = GenNormal(list, Rook, quietChecks, targets, size);
                size = GenNormal(list, Queen, quietChecks, targets, size);
            }

            //  If we are doing non-captures with check and our king isn't blocking a check, then skip generating king moves
            if (!(quietChecks && (State->BlockingPieces[Not(ToMove)] & SquareBB[ourKing]) == 0))
            {
                ulong moves = NeighborsMask[ourKing] & (evasions ? ~us : targets);
                if (quietChecks)
                {
                    //  If we ARE doing non-captures with check and our king is a blocker,
                    //  then only generate moves that get the king off of any shared ranks/files.
                    //  Note we can't move our king from one shared ray to another since we can only move diagonally 1 square
                    //  and their king would be attacking ours.
                    moves &= ~bb.AttackMask(theirKing, Not(ToMove), Queen, occ);
                }

                while (moves != 0)
                {
                    int to = poplsb(&moves);

                    ref Move m = ref list[size++].Move;
                    m.SetNew(ourKing, to, (them & SquareBB[to]) != 0);

                }

                if ((quiets || nonEvasions) && ((State->CastleStatus & (ToMove == White ? CastlingStatus.White : CastlingStatus.Black)) != CastlingStatus.None))
                {
                    //  Only do castling moves if we are doing non-captures or we aren't in check.
                    size = GenCastlingMoves(list, size);
                }
            }

            return size;

            int GenCastlingMoves(ScoredMove* list, int size)
            {
                if (ToMove == White && ourKing == E1)
                {
                    if (State->CastleStatus.HasFlag(CastlingStatus.WK)
                        && (occ & WhiteKingsideMask) == 0
                        && (bb.AttackersTo(F1, occ) & them) == 0
                        && (bb.AttackersTo(G1, occ) & them) == 0
                        && (bb.Pieces[Rook] & SquareBB[H1] & us) != 0)
                    {
                        ref Move m = ref list[size++].Move;
                        m.SetNew(E1, G1);
                        m.Castle = true;
                    }

                    if (State->CastleStatus.HasFlag(CastlingStatus.WQ)
                        && (occ & WhiteQueensideMask) == 0
                        && (bb.AttackersTo(C1, occ) & them) == 0
                        && (bb.AttackersTo(D1, occ) & them) == 0
                        && (bb.Pieces[Rook] & SquareBB[A1] & us) != 0)
                    {
                        ref Move m = ref list[size++].Move;
                        m.SetNew(E1, C1);
                        m.Castle = true;
                    }
                }
                else if (ToMove == Black && ourKing == E8)
                {
                    if (State->CastleStatus.HasFlag(CastlingStatus.BK)
                        && (occ & BlackKingsideMask) == 0
                        && (bb.AttackersTo(F8, occ) & them) == 0
                        && (bb.AttackersTo(G8, occ) & them) == 0
                        && (bb.Pieces[Rook] & SquareBB[H8] & us) != 0)
                    {
                        ref Move m = ref list[size++].Move;
                        m.SetNew(E8, G8);
                        m.Castle = true;
                    }

                    if (State->CastleStatus.HasFlag(CastlingStatus.BQ)
                        && (occ & BlackQueensideMask) == 0
                        && (bb.AttackersTo(C8, occ) & them) == 0
                        && (bb.AttackersTo(D8, occ) & them) == 0
                        && (bb.Pieces[Rook] & SquareBB[A8] & us) != 0)
                    {
                        ref Move m = ref list[size++].Move;
                        m.SetNew(E8, C8);
                        m.Castle = true;
                    }
                }

                return size;
            }
        }


        /// <summary>
        /// Generates all of the legal moves that the player whose turn it is to move is able to make.
        /// The moves are placed into the array that <paramref name="legal"/> points to, 
        /// and the number of moves that were created is returned.
        /// </summary>
        public int GenLegal(ScoredMove* legal)
        {
            int numMoves = (State->Checkers != 0) ? GenAll<GenEvasions>(legal) :
                                                    GenAll<GenNonEvasions>(legal);

            int ourKing = State->KingSquares[ToMove];
            int theirKing = State->KingSquares[Not(ToMove)];
            ulong pinned = State->BlockingPieces[ToMove];

            ScoredMove* curr = legal;
            ScoredMove* end = legal + numMoves;

            while (curr != end)
            {
                if (!IsLegal(curr->Move, ourKing, theirKing, pinned))
                {
                    *curr = *--end;
                    numMoves--;
                }
                else
                {
                    ++curr;
                }
            }

            return numMoves;
        }


        /// <summary>
        /// Generates the pseudo-legal evasion or non-evasion moves for the position, depending on if the side to move is in check.
        /// The moves are placed into the array that <paramref name="pseudo"/> points to, 
        /// and the number of moves that were created is returned.
        /// </summary>
        [MethodImpl(Inline)]
        public int GenPseudoLegal(ScoredMove* pseudo)
        {
            return (State->Checkers != 0) ? GenAll<GenEvasions>(pseudo) :
                                            GenAll<GenNonEvasions>(pseudo);
        }


        /// <summary>
        /// Generates the pseudo-legal moves for all of the pieces of type <paramref name="pt"/>, placing them into the 
        /// ScoredMove <paramref name="list"/> starting at the index <paramref name="size"/> and returning the new number
        /// of moves in the list.
        /// <para></para>
        /// Only moves which have a To square whose bit is set in <paramref name="targets"/> will be generated.
        /// <br></br>
        /// For example:
        /// <br></br>
        /// When generating captures, <paramref name="targets"/> should be set to our opponent's color mask.
        /// <br></br>
        /// When generating evasions, <paramref name="targets"/> should be set to the <see cref="LineBB"/> between our king and the checker, which is the mask
        /// of squares that would block the check or capture the piece giving check.
        /// <para></para>
        /// If <paramref name="checks"/> is true, then only pseudo-legal moves that give check will be generated.
        /// </summary>
        public int GenNormal(ScoredMove* list, int pt, bool checks, ulong targets, int size)
        {
            // TODO: JIT seems to prefer having separate methods for each piece type, instead of a 'pt' parameter
            // This is far more convenient though

            ulong us = bb.Colors[ToMove];
            ulong them = bb.Colors[Not(ToMove)];
            ulong occ = us | them;

            ulong ourPieces = bb.Pieces[pt] & bb.Colors[ToMove];
            while (ourPieces != 0)
            {
                int idx = poplsb(&ourPieces);
                ulong moves = bb.AttackMask(idx, ToMove, pt, occ) & targets;

                if (checks && (pt == Queen || ((State->BlockingPieces[Not(ToMove)] & SquareBB[idx]) == 0)))
                {
                    moves &= State->CheckSquares[pt];
                }

                while (moves != 0)
                {
                    int to = poplsb(&moves);

                    ref Move m = ref list[size++].Move;
                    m.SetNew(idx, to, capture: (them & SquareBB[to]) != 0);
                }
            }

            return size;
        }



        /// <summary>
        /// <inheritdoc cref="GenLegal(ScoredMove*)"/>
        /// </summary>
        [MethodImpl(Inline)]
        public int GenLegal(Span<ScoredMove> legal)
        {
            //  The Span that this method receives is almost certainly already pinned (created via 'stackalloc'),
            //  but fixing it here is essentially free performance-wise and lets us use Span's when possible.
            fixed (ScoredMove* ptr = legal)
            {
                return GenLegal(ptr);
            }
        }


        /// <summary>
        /// <inheritdoc cref="GenPseudoLegal(ScoredMove*)"/>
        /// </summary>
        [MethodImpl(Inline)]
        public int GenPseudoLegal(Span<ScoredMove> pseudo)
        {
            fixed (ScoredMove* ptr = pseudo)
            {
                return GenPseudoLegal(ptr);
            }

        }


        /// <summary>
        /// <inheritdoc cref="GenAll{GenType}(ScoredMove*, int)"/>
        /// </summary>
        public int GenAll<GenType>(Span<ScoredMove> list, int size = 0) where GenType : MoveGenerationType
        {
            fixed (ScoredMove* ptr = list)
            {
                return GenAll<GenType>(ptr, size);
            }
        }

#if OLD
        /// <summary>
        /// Determines if the Move <paramref name="m"/> will put the enemy king in check or double check
        /// and updates <paramref name="m"/>'s check information.
        /// </summary>
        public void MakeCheck(int pt, ref Move m)
        {
            int ourColor = ToMove;
            int theirKing = State->KingSquares[Not(ToMove)];

            int moveFrom = m.From;
            int moveTo = m.To;

            //  State->CheckSquares[King == 5] is OOB, and kings can't directly check anyways...
            if (pt != King && (State->CheckSquares[pt] & SquareBB[moveTo]) != 0)
            {
                //  This piece is making a direct check
                m.CausesCheck = true;
                m.SqChecker = moveTo;
            }

            if ((State->BlockingPieces[Not(ourColor)] & SquareBB[moveFrom]) != 0)
            {
                //  This piece is blocking a check on their king
                if (((RayBB[moveFrom][moveTo] & SquareBB[theirKing]) == 0) || m.Castle)
                {
                    //  If it moved off of the ray that it was blocking the check on,
                    //  then it is causing a discovery

                    if (m.CausesCheck)
                    {
                        //  If the piece that had been blocking also moved to one of the CheckSquares,
                        //  then this is actually double check.

                        m.CausesCheck = false;
                        m.CausesDoubleCheck = true;
                    }
                    else
                    {
                        m.CausesCheck = true;
                    }

                    if (EnableAssertions)
                    {
                        Assert((State->Xrays[ourColor] & XrayBB[theirKing][moveFrom]) != 0,
                            "The piece causing a discovered check should be aligned on the XrayBB between "
                            + theirKing + " and " + moveFrom + " (which is " + XrayBB[theirKing][moveFrom] + ")."
                            + "This ray should have shared a bit with something in State->Xrays[" + ColorToString(ourColor) + "] (which is " + State->Xrays[ourColor] + "),"
                            + "but these bitboards are 0 when AND'd! (should give an lsb of 0 <= bb <= 63, not 64 == SquareNB)");
                    }

                    //  The piece causing the discovery is the xrayer of our color 
                    //  that is on the same ray that the piece we were moving shared with the king.
                    m.SqChecker = lsb(State->Xrays[ourColor] & XrayBB[theirKing][moveFrom]);

                }
            }

            //  En passant, promotions, and castling checks are already handled
        }


        public void MakeCheckPromotion(ref Move m)
        {
            int theirKing = State->KingSquares[Not(ToMove)];

            int from = m.From;
            int promotionSquare = m.To;
            int promotionPiece = m.PromotionTo;

            if ((bb.AttackMask(promotionSquare, ToMove, promotionPiece, bb.Occupancy ^ SquareBB[from]) & SquareBB[theirKing]) != 0)
            {
                m.CausesCheck = true;
                m.SqChecker = promotionSquare;
            }

            if ((State->BlockingPieces[Not(ToMove)] & SquareBB[from]) != 0)
            {
                //  This piece is blocking a check on their king
                if ((RayBB[from][promotionSquare] & SquareBB[theirKing]) == 0)
                {
                    //  If it moved off of the ray that it was blocking the check on,
                    //  then it is causing a discovery

                    if (m.CausesCheck)
                    {
                        //  If the piece that had been blocking also moved to one of the CheckSquares,
                        //  then this is actually double check.

                        m.CausesCheck = false;
                        m.CausesDoubleCheck = true;
                    }
                    else
                    {
                        m.CausesCheck = true;
                    }

                    if (EnableAssertions)
                    {
                        Assert((State->Xrays[ToMove] & XrayBB[theirKing][from]) != 0,
                            "The piece causing a discovered check should be aligned on the XrayBB between "
                            + theirKing + " and " + from + " (which is " + XrayBB[theirKing][from] + ")."
                            + "This ray should have shared a bit with something in State->Xrays[" + ColorToString(ToMove) + "] (which is " + State->Xrays[ToMove] + "),"
                            + "but these bitboards are 0 when AND'd! (should give an lsb of 0 <= bb <= 63, not 64 == SquareNB)");
                    }

                    //  The piece causing the discovery is the xrayer of our color 
                    //  that is on the same ray that the piece we were moving shared with the king.
                    m.SqChecker = lsb(State->Xrays[ToMove] & XrayBB[theirKing][from]);
                }
            }
        }


        public void MakeCheckCastle(ref Move m)
        {
            int rookTo = m.To switch
            {
                C1 => D1,
                G1 => F1,
                C8 => D8,
                _ => F8,    //  G8 => F8
            };

            int theirKing = State->KingSquares[Not(ToMove)];

            ulong between = BetweenBB[rookTo][theirKing];
            if (between != 0 &&
                ((between & (bb.Occupancy ^ bb.KingMask(ToMove))) == 0) &&
                (GetIndexFile(rookTo) == GetIndexFile(theirKing) || GetIndexRank(rookTo) == GetIndexRank(theirKing)))
            {
                //  Then their king is on the same rank/file/diagonal as the square that our rook will end up at,
                //  and there are no pieces which are blocking that ray.

                m.CausesCheck = true;
                m.SqChecker = rookTo;
            }
        }


        public void MakeCheckEnPassant(ref Move m)
        {

            int up = ShiftUpDir(ToMove);
            ulong moveMask = SquareBB[m.From] | SquareBB[State->EPSquare];
            bb.Pieces[Piece.Pawn] ^= moveMask | SquareBB[State->EPSquare - up];
            bb.Colors[ToMove] ^= moveMask;
            bb.Colors[Not(ToMove)] ^= SquareBB[State->EPSquare - up];

            ulong attacks = bb.AttackersTo(State->KingSquares[Not(ToMove)], bb.Occupancy) & bb.Colors[ToMove];

            switch (popcount(attacks))
            {
                case 0:
                    break;
                case 1:
                    m.CausesCheck = true;
                    m.SqChecker = lsb(attacks);
                    break;
                case 2:
                    m.CausesDoubleCheck = true;
                    m.SqChecker = lsb(attacks);
                    break;
            }

            bb.Pieces[Piece.Pawn] ^= moveMask | SquareBB[State->EPSquare - up];
            bb.Colors[ToMove] ^= moveMask;
            bb.Colors[Not(ToMove)] ^= SquareBB[State->EPSquare - up];
        }
#endif

        [MethodImpl(Inline)]
        public ScoredMove CondensedToNormalMove(CondensedMove cm)
        {
            Move m = new Move(cm);

            if (m.Promotion && bb.GetPieceAtIndex(m.From) != Pawn)
            {
                m.Promotion = false;
            }

            if (bb.GetPieceAtIndex(m.To) != None)
            {
                m.Capture = true;
            }

            return new ScoredMove(ref m);
        }
    }
}
