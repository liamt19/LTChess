﻿using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

using LTChess.Logic.Data;
using LTChess.Logic.NN;
using LTChess.Logic.NN.HalfKA_HM;
using LTChess.Logic.NN.Simple768;
using LTChess.Logic.Threads;

namespace LTChess.Logic.Core
{
    public unsafe partial class Position
    {
        /// <summary>
        /// This Position's <see cref="Bitboard"/>, which contains masks for each type of piece and player color.
        /// </summary>
        public Bitboard bb;

        /// <summary>
        /// The address of <see cref="bb"/>'s memory block, since C# doesn't like taking the address of structs.
        /// </summary>
        private readonly nint _bbBlock;

        /// <summary>
        /// The second number in the FEN, which starts at 1 and increases every time black moves.
        /// </summary>
        public int FullMoves = 1;

        public CheckInfo CheckInfo;

        /// <summary>
        /// Set to the color of the player whose turn it is to move.
        /// </summary>
        public int ToMove;

        /// <summary>
        /// Current zobrist hash of the position.
        /// </summary>
        public ulong Hash;

        /// <summary>
        /// The sum of all material in the position, indexed by piece color.
        /// </summary>
        public int[] MaterialCount;

        /// <summary>
        /// The sum of the non-pawn material in the position, indexed by piece color.
        /// </summary>
        public int[] MaterialCountNonPawn;

        public bool Checked => (CheckInfo.InCheck || CheckInfo.InDoubleCheck);


        /// <summary>
        /// The number of <see cref="StateInfo"/> items that memory will be allocated for within the StateStack, which is 256 KB.
        /// If you find yourself in a game exceeding 2047 moves, go outside.
        /// </summary>
        public const int StateStackSize = 2048;

        private readonly nint _stateBlock;

        /// <summary>
        /// A pointer to the beginning of the StateStack, which is used to make sure we don't try to access the StateStack at negative indices.
        /// </summary>
        private readonly StateInfo* _SentinelStart;
        private readonly StateInfo* _SentinelEnd;

        /// <summary>
        /// The initial StateInfo.
        /// </summary>
        public StateInfo* StartingState => (_SentinelStart);

        /// <summary>
        /// A pointer to this Position's current <see cref="StateInfo"/> object, which corresponds to the StateStack[GamePly]
        /// </summary>
        public StateInfo* State;

        /// <summary>
        /// The StateInfo before the current one, or NULL if the current StateInfo is the first one, <see cref="_SentinelStart"/>
        /// </summary>
        public StateInfo* PreviousState => (State == _SentinelStart ? (null) : (State - 1));

        /// <summary>
        /// The StateInfo after the current one, which hopefully is within the bounds of <see cref="StateStackSize"/>
        /// </summary>
        public StateInfo* NextState
        {
            get
            {
                if (State == _SentinelEnd)
                {
                    Log("ERROR: Current State is _SentinelEnd, and getting NextState is OOB!");
                    return null;
                }

                return (State + 1);
            }
        }



        private readonly nint _accumulatorBlock = nint.Zero;


        /// <summary>
        /// The number of moves that have been made so far since this Position was created.
        /// <br></br>
        /// Only used to easily keep track of how far along the StateStack we currently are.
        /// </summary>
        public int GamePly = 0;

        /// <summary>
        /// The SearchThread that owns this Position instance.
        /// </summary>
        public readonly SearchThread Owner;

        /// <summary>
        /// Whether or not to incrementally update accumulators when making/unmaking moves.
        /// This must be true if this position object is being used in a search, but
        /// 
        /// </summary>
        public readonly bool UpdateNN;


        /// <summary>
        /// Creates a new Position object and loads the provided FEN.
        /// <br></br>
        /// If <paramref name="createAccumulators"/> is true, then this Position will create and incrementally update 
        /// its accumulators when making and unmaking moves.
        /// <para></para>
        /// <paramref name="owner"/> should be set to one of the <see cref="SearchThread"/>'s within the <see cref="SearchThreadPool.SearchPool"/>
        /// (the <see cref="SearchThreadPool.MainThread"/> unless there are multiple threads in the pool).
        /// <br></br>
        /// If <paramref name="owner"/> is <see langword="null"/> then <paramref name="createAccumulators"/> should be false.
        /// </summary>
        public Position(string fen = InitialFEN, bool createAccumulators = true, SearchThread? owner = null)
        {
            MaterialCount = new int[2];
            MaterialCountNonPawn = new int[2];

            this.UpdateNN = createAccumulators;
            this.Owner = owner;

            _bbBlock = (nint) AlignedAllocZeroed((nuint)(sizeof(Bitboard) * 1), AllocAlignment);
            this.bb = *(Bitboard*)_bbBlock;

            _stateBlock = (nint)AlignedAllocZeroed((nuint)(sizeof(StateInfo) * StateStackSize), AllocAlignment);
            StateInfo* StateStack = (StateInfo*)_stateBlock;

            _SentinelStart = &StateStack[0];
            _SentinelEnd = &StateStack[StateStackSize - 1];
            State = &StateStack[0];

            if (UpdateNN)
            {
                //  Create the accumulators now if we need to.
                //  This is actually a rather significant memory investment (each AccumulatorPSQT needs 6,216 = ~6kb of memory)
                //  so this constructor should be called as infrequently as possible to keep the memory usage from spiking
                _accumulatorBlock = (nint) AlignedAllocZeroed((nuint)(sizeof(AccumulatorPSQT) * StateStackSize), AllocAlignment);
                AccumulatorPSQT* accs = (AccumulatorPSQT*) _accumulatorBlock;
                for (int i = 0; i < StateStackSize; i++)
                {
                    (StateStack + i)->Accumulator = (accs + i);
                }

                for (int i = 0; i < StateStackSize; i++)
                {
                    *((StateStack + i)->Accumulator) = new AccumulatorPSQT();
                }
            }


            if (UpdateNN && Owner == null)
            {
                Log("WARN Position('" + fen + "', " + createAccumulators + ", ...) has ResetNN true and was given a nullptr for owner! (if ResetNN is true, an owner must be provided)");
                Log("Assigning this Position instance to the SearchPool's MainThread, UB and other weirdness may occur...");
                Owner = SearchPool.MainThread;
            }

            LoadFromFEN(fen);

            if (UseSimple768 && createAccumulators)
            {
                NNUEEvaluation.RefreshNN(this);
                NNUEEvaluation.ResetNN();
            }

            if (UseHalfKA && UpdateNN)
            {
                State->Accumulator->RefreshPerspective[White] = true;
                State->Accumulator->RefreshPerspective[Black] = true;
            }
        }


        /// <summary>
        /// Free the memory block that we allocated for this Position's <see cref="Bitboard"/>
        /// </summary>
        ~Position()
        {
            NativeMemory.AlignedFree((void*) _bbBlock);

            if (UpdateNN)
            {
                //  Free each accumulator, then the block
                for (int i = 0; i < StateStackSize; i++)
                {
                    var acc = *((_SentinelStart + i)->Accumulator);
                    acc.Dispose();
                }

                NativeMemory.AlignedFree((void*)_accumulatorBlock);
            }

            NativeMemory.AlignedFree((void*) _stateBlock);
        }

        /// <summary>
        /// Generates the legal moves in the current position and makes the move that corresponds to the <paramref name="moveStr"/> if one exists.
        /// </summary>
        /// <param name="moveStr">The Algebraic notation for a move i.e. Nxd4+, or the Smith notation i.e. e2e4</param>
        /// <returns>True if <paramref name="moveStr"/> was a recognized and legal move.</returns>
        public bool TryMakeMove(string moveStr)
        {
            Span<ScoredMove> list = stackalloc ScoredMove[MoveListSize];
            int size = GenLegal(list);
            for (int i = 0; i < size; i++)
            {
                Move m = list[i].Move;
                if (m.ToString(this).ToLower().Equals(moveStr.ToLower()) || m.ToString().ToLower().Equals(moveStr.ToLower()))
                {
                    MakeMove(m);
                    return true;
                }
                if (i == size - 1)
                {
                    Log("No move '" + moveStr + "' found, try one of the following: ");
                    Log(Stringify(list, this) + "\r\n" + Stringify(list));
                }
            }
            return false;
        }

        /// <summary>
        /// Generates the legal moves in the current position and makes the move that corresponds to the <paramref name="moveStr"/> if one exists.
        /// </summary>
        /// <param name="moveStr">The Algebraic notation for a move i.e. Nxd4+, or the Smith notation i.e. e2e4</param>
        /// <returns>True if <paramref name="moveStr"/> was a recognized and legal move.</returns>
        public bool TryFindMove(string moveStr, out Move move)
        {
            Span<ScoredMove> list = stackalloc ScoredMove[MoveListSize];
            int size = GenLegal(list);
            for (int i = 0; i < size; i++)
            {
                Move m = list[i].Move;
                if (m.ToString(this).ToLower().Equals(moveStr.ToLower()) || m.ToString().ToLower().Equals(moveStr.ToLower()))
                {
                    move = m;
                    return true;
                }
                if (i == size - 1)
                {
                    Log("No move '" + moveStr + "' found, try one of the following: ");
                    Log(Stringify(list, this) + "\r\n" + Stringify(list));
                }
            }

            move = Move.Null;
            return false;
        }

        /// <summary>
        /// Copies the current state into the <paramref name="newState"/>, then performs the move <paramref name="move"/>.
        /// <br></br>
        /// If <see cref="UpdateNN"/> is true, this method will also update the NNUE networks.
        /// </summary>
        /// <param name="move">The move to make, which needs to be a legal move or strange things might happen.</param>
        [MethodImpl(Inline)]
        public void MakeMove(Move move)
        {
            //  Copy everything except the pointer to the accumulator, which should never change.
            //  The data within the accumulator will be copied, but each state needs its own pointer to its own accumulator.
            Unsafe.CopyBlockUnaligned((State + 1), State, (uint) StateInfo.StateCopySize);

            if (UseHalfKA && UpdateNN)
            {
                HalfKA_HM.MakeMove(this, move);
            }

            //  Move onto the next state
            State++;

            State->HalfmoveClock++;
            GamePly++;

            if (UseSimple768 && UpdateNN)
            {
                NNUEEvaluation.MakeMoveNN(this, move);
            }

            if (ToMove == Color.Black)
            {
                FullMoves++;
            }

            int moveFrom = move.From;
            int moveTo = move.To;

            int ourPiece = bb.GetPieceAtIndex(moveFrom);
            int ourColor = bb.GetColorAtIndex(moveFrom);

            int theirPiece = bb.GetPieceAtIndex(moveTo);
            int theirColor = Not(ourColor);

            if (EnableAssertions)
            {
                Assert(ourPiece != Piece.None, 
                    "Move " + move.ToString() + " in FEN '" + GetFEN() + "' doesn't have a piece on the From square!");

                Assert((theirPiece == None || ourColor != bb.GetColorAtIndex(moveTo)), 
                    "Move " + move.ToString(this) + " in FEN '" + GetFEN() + "' is trying to capture our own " + PieceToString(theirPiece) + " on " + IndexToString(moveTo));
                
                Assert(theirPiece != King, 
                    "Move " + move.ToString(this) + " in FEN '" + GetFEN() + "' is trying to capture " + ColorToString(bb.GetColorAtIndex(moveTo)) + "'s king on " + IndexToString(moveTo));

                Assert(ourColor == ToMove,
                    "Move " + move.ToString(this) + " in FEN '" + GetFEN() + "' is trying to move a " + ColorToString(ourColor) +
                    " piece when it is " + ColorToString(ToMove) + "'s turn to move!");
            }

            if (ourPiece == Piece.King)
            {
                State->KingSquares[ourColor] = moveTo;

                if (move.Castle)
                {
                    //  Move our rook and update the hash
                    switch (moveTo)
                    {
                        case C1:
                            bb.MoveSimple(A1, D1, ourColor, Piece.Rook);
                            Hash = Hash.ZobristMove(A1, D1, ourColor, Piece.Rook);
                            break;
                        case G1:
                            bb.MoveSimple(H1, F1, ourColor, Piece.Rook);
                            Hash = Hash.ZobristMove(H1, F1, ourColor, Piece.Rook);
                            break;
                        case C8:
                            bb.MoveSimple(A8, D8, ourColor, Piece.Rook);
                            Hash = Hash.ZobristMove(A8, D8, ourColor, Piece.Rook);
                            break;
                        default:
                            bb.MoveSimple(H8, F8, ourColor, Piece.Rook);
                            Hash = Hash.ZobristMove(H8, F8, ourColor, Piece.Rook);
                            break;
                    }
                }

                //  Remove all of our castling rights
                if (ourColor == Color.White)
                {
                    Hash = Hash.ZobristCastle(State->CastleStatus, (CastlingStatus.WK | CastlingStatus.WQ));
                    State->CastleStatus &= ~(CastlingStatus.WK | CastlingStatus.WQ);
                }
                else
                {
                    Hash = Hash.ZobristCastle(State->CastleStatus, (CastlingStatus.BK | CastlingStatus.BQ));
                    State->CastleStatus &= ~(CastlingStatus.BK | CastlingStatus.BQ);
                }
            }
            else if (ourPiece == Piece.Rook && State->CastleStatus != CastlingStatus.None)
            {
                //  If we just moved a rook, update st->CastleStatus
                switch (moveFrom)
                {
                    case A1:
                        Hash = Hash.ZobristCastle(State->CastleStatus, CastlingStatus.WQ);
                        State->CastleStatus &= ~CastlingStatus.WQ;
                        break;
                    case H1:
                        Hash = Hash.ZobristCastle(State->CastleStatus, CastlingStatus.WK);
                        State->CastleStatus &= ~CastlingStatus.WK;
                        break;
                    case A8:
                        Hash = Hash.ZobristCastle(State->CastleStatus, CastlingStatus.BQ);
                        State->CastleStatus &= ~CastlingStatus.BQ;
                        break;
                    case H8:
                        Hash = Hash.ZobristCastle(State->CastleStatus, CastlingStatus.BK);
                        State->CastleStatus &= ~CastlingStatus.BK;
                        break;
                }
            }

            if (move.Capture)
            {
                //  Remove their piece, and update the hash
                bb.RemovePiece(moveTo, theirColor, theirPiece);
                Hash = Hash.ZobristToggleSquare(theirColor, theirPiece, moveTo);

                if (theirPiece == Piece.Rook)
                {
                    //  If we are capturing a rook, make sure that if that we remove that castling status from them if necessary.
                    switch (moveTo)
                    {
                        case A1:
                            Hash = Hash.ZobristCastle(State->CastleStatus, CastlingStatus.WQ);
                            State->CastleStatus &= ~CastlingStatus.WQ;
                            break;
                        case H1:
                            Hash = Hash.ZobristCastle(State->CastleStatus, CastlingStatus.WK);
                            State->CastleStatus &= ~CastlingStatus.WK;
                            break;
                        case A8:
                            Hash = Hash.ZobristCastle(State->CastleStatus, CastlingStatus.BQ);
                            State->CastleStatus &= ~CastlingStatus.BQ;
                            break;
                        case H8:
                            Hash = Hash.ZobristCastle(State->CastleStatus, CastlingStatus.BK);
                            State->CastleStatus &= ~CastlingStatus.BK;
                            break;
                    }
                }

                MaterialCount[theirColor] -= GetPieceValue(theirPiece);

                if (theirPiece != Pawn)
                {
                    MaterialCountNonPawn[theirColor] -= GetPieceValue(theirPiece);
                }

                State->CapturedPiece = theirPiece;

                //  Reset the halfmove clock
                State->HalfmoveClock = 0;
            }
            else
            {
                State->CapturedPiece = None;
            }


            int tempEPSquare = State->EPSquare;

            if (State->EPSquare != EPNone)
            {
                //  Set st->EPSquare to 64 now.
                //  If we are capturing en passant, move.EnPassant is true. In any case it should be reset every move.
                Hash = Hash.ZobristEnPassant(GetIndexFile(State->EPSquare));
                State->EPSquare = EPNone;
            }

            if (ourPiece == Piece.Pawn)
            {
                if (move.EnPassant)
                {
                    if (EnableAssertions)
                    {
                        Assert(tempEPSquare != EPNone, 
                            "MakeMove(" + move.ToString(this) + ") is an en passant move, " +
                            "but the current position doesn't have a pawn that can be captured via en passant!" +
                            "(State->EPSquare was " + EPNone + ", should be A3 <= EpSquare <= H6)");
                    }

                    int idxPawn = ((bb.Pieces[Piece.Pawn] & SquareBB[tempEPSquare - 8]) != 0) ? tempEPSquare - 8 : tempEPSquare + 8;
                    bb.RemovePiece(idxPawn, theirColor, Piece.Pawn);
                    Hash = Hash.ZobristToggleSquare(theirColor, Piece.Pawn, idxPawn);

                    //  The EnPassant/Capture flags are mutually exclusive, so set CapturedPiece here
                    State->CapturedPiece = Piece.Pawn;

                    MaterialCount[theirColor] -= GetPieceValue(Piece.Pawn);
                }
                else if ((moveTo ^ moveFrom) == 16)
                {
                    //  st->EPSquare is only set if they have a pawn that can capture this one (via en passant)
                    if (ourColor == Color.White && (WhitePawnAttackMasks[moveTo - 8] & (bb.Colors[Color.Black] & bb.Pieces[Piece.Pawn])) != 0)
                    {
                        State->EPSquare = moveTo - 8;
                    }
                    else if (ourColor == Color.Black && (BlackPawnAttackMasks[moveTo + 8] & (bb.Colors[Color.White] & bb.Pieces[Piece.Pawn])) != 0)
                    {
                        State->EPSquare = moveTo + 8;
                    }

                    if (State->EPSquare != EPNone)
                    {
                        //  Update the En Passant file if we just changed st->EPSquare
                        Hash = Hash.ZobristEnPassant(GetIndexFile(State->EPSquare));
                    }
                }

                //  Reset the halfmove clock
                State->HalfmoveClock = 0;
            }


            bb.MoveSimple(moveFrom, moveTo, ourColor, ourPiece);
            Hash = Hash.ZobristMove(moveFrom, moveTo, ourColor, ourPiece);

            if (EnableAssertions)
            {
                Assert(popcount(bb.Colors[ourColor]) <= 16,
                    "MakeMove(" + move + ") caused " + ColorToString(ourColor) + " to have " + popcount(bb.Colors[ourColor]) + " > 16 pieces!");

                Assert(popcount(bb.Colors[theirColor]) <= 16,
                    "MakeMove(" + move + ") caused " + ColorToString(theirColor) + " to have " + popcount(bb.Colors[ourColor]) + " > 16 pieces!");
            }

            if (move.Promotion)
            {
                //  Get rid of the pawn we just put there
                bb.RemovePiece(moveTo, ourColor, ourPiece);

                //  And replace it with the promotion piece
                bb.AddPiece(moveTo, ourColor, move.PromotionTo);

                Hash = Hash.ZobristToggleSquare(ourColor, ourPiece, moveTo);
                Hash = Hash.ZobristToggleSquare(ourColor, move.PromotionTo, moveTo);

                MaterialCount[ourColor] -= GetPieceValue(Piece.Pawn);
                MaterialCount[ourColor] += GetPieceValue(move.PromotionTo);
                MaterialCountNonPawn[ourColor] += GetPieceValue(move.PromotionTo);
            }

            if (EnableAssertions && move.Checks)
            {
                Assert((bb.AttackersTo(State->KingSquares[Not(ToMove)], bb.Occupancy) & bb.Colors[ToMove]) != 0,
                    "The move " + move + " is marked as causing " + (move.CausesDoubleCheck ? "double" : string.Empty) + " check, " + 
                    "but there are no attackers to their king's square after the move was made!");
            }

            if (move.CausesCheck)
            {
                CheckInfo.idxChecker = move.SqChecker;
                CheckInfo.InCheck = true;
                CheckInfo.InDoubleCheck = false;
            }
            else if (move.CausesDoubleCheck)
            {
                CheckInfo.InCheck = false;
                CheckInfo.idxChecker = move.SqChecker;
                CheckInfo.InDoubleCheck = true;
            }
            else
            {
                //	We are either getting out of a check, or weren't in check at all
                CheckInfo.InCheck = false;
                CheckInfo.idxChecker = LSBEmpty;
                CheckInfo.InDoubleCheck = false;
            }

            Hash = Hash.ZobristChangeToMove();
            ToMove = Not(ToMove);

            State->Hash = Hash;
            if (move.Checks)
            {
                State->Checkers = bb.AttackersTo(State->KingSquares[theirColor], bb.Occupancy) & bb.Colors[ourColor];
            }
            else
            {
                State->Checkers = 0;
            }

            SetCheckInfo();
        }

        
        [MethodImpl(Inline)]
        public void UnmakeMove(Move move)
        {
            int moveFrom = move.From;
            int moveTo = move.To;

            //  Assume that "we" just made the last move, and "they" are undoing it.
            int ourPiece = bb.GetPieceAtIndex(moveTo);
            int ourColor = Not(ToMove);
            int theirColor = ToMove;

            GamePly--;

            if (move.Promotion)
            {
                //  Remove the promotion piece and replace it with a pawn
                bb.RemovePiece(moveTo, ourColor, ourPiece);

                ourPiece = Piece.Pawn;

                bb.AddPiece(moveTo, ourColor, ourPiece);

                MaterialCount[ourColor] -= GetPieceValue(move.PromotionTo);
                MaterialCount[ourColor] += GetPieceValue(Piece.Pawn);

                MaterialCountNonPawn[ourColor] -= GetPieceValue(move.PromotionTo);
            }
            else if (move.Castle)
            {
                //  Put the rook back
                if (moveTo == C1)
                {
                    bb.MoveSimple(D1, A1, ourColor, Piece.Rook);
                }
                else if (moveTo == G1)
                {
                    bb.MoveSimple(F1, H1, ourColor, Piece.Rook);
                }
                else if (moveTo == C8)
                {
                    bb.MoveSimple(D8, A8, ourColor, Piece.Rook);
                }
                else
                {
                    //  moveTo == G8
                    bb.MoveSimple(F8, H8, ourColor, Piece.Rook);
                }

            }

            //  Put our piece back to the square it came from.
            bb.MoveSimple(moveTo, moveFrom, ourColor, ourPiece);

            if (State->CapturedPiece != Piece.None)
            {
                //  CapturedPiece is set for captures and en passant, so check which it was
                if (move.EnPassant)
                {
                    //  If the move was an en passant, put the captured pawn back

                    int idxPawn = moveTo + ShiftUpDir(ToMove);
                    bb.AddPiece(idxPawn, theirColor, Piece.Pawn);

                    MaterialCount[theirColor] += GetPieceValue(Piece.Pawn);
                }
                else
                {
                    //  Otherwise it was a capture, so put the captured piece back
                    bb.AddPiece(moveTo, theirColor, State->CapturedPiece);

                    MaterialCount[theirColor] += GetPieceValue(State->CapturedPiece);
                    if (State->CapturedPiece != Pawn)
                    {
                        MaterialCountNonPawn[theirColor] += GetPieceValue(State->CapturedPiece);
                    }
                }
            }

            if (ourColor == Color.Black)
            {
                //  If ourColor == Color.Black (and ToMove == White), then we incremented FullMoves when this move was made.
                FullMoves--;
            }

            State--;
            this.Hash = State->Hash;

            switch (popcount(State->Checkers))
            {
                case 0:
                    CheckInfo.InCheck = false;
                    CheckInfo.InDoubleCheck = false;
                    CheckInfo.idxChecker = LSBEmpty;
                    break;
                case 1:
                    CheckInfo.InCheck = true;
                    CheckInfo.InDoubleCheck = false;
                    CheckInfo.idxChecker = lsb(State->Checkers);
                    break;
                case 2:
                    CheckInfo.InCheck = false;
                    CheckInfo.InDoubleCheck = true;
                    CheckInfo.idxChecker = lsb(State->Checkers);
                    break;
            }


            ToMove = Not(ToMove);

            if (UseSimple768 && UpdateNN)
            {
                NNUEEvaluation.UnmakeMoveNN();
            }

        }


        /// <summary>
        /// Performs a null move, which only updates the EnPassantTarget (since it is always reset to 64 when a move is made) 
        /// and position hash accordingly.
        /// </summary>
        [MethodImpl(Inline)]
        public void MakeNullMove()
        {
            //  Copy everything except the pointer to the accumulator, which should never change.
            Unsafe.CopyBlockUnaligned((State + 1), State, (uint) StateInfo.StateCopySize);
            State->Accumulator->CopyTo(NextState->Accumulator);

            State++;
            
            if (State->EPSquare != EPNone)
            {
                //  Set EnPassantTarget to 64 now.
                //  If we are capturing en passant, move.EnPassant is true. In any case it should be reset every move.
                Hash = Hash.ZobristEnPassant(GetIndexFile(State->EPSquare));
                State->EPSquare = EPNone;
            }

            Hash = Hash.ZobristChangeToMove();
            ToMove = Not(ToMove);
            State->Hash = Hash;
            State->HalfmoveClock++;

            SetCheckInfo();

            prefetch(TranspositionTable.GetCluster(Hash));

        }

        /// <summary>
        /// Undoes a null move, which returns EnPassantTarget and the hash to their previous values.
        /// </summary>
        [MethodImpl(Inline)]
        public void UnmakeNullMove()
        {
            State--;

            Hash = State->Hash;
            ToMove = Not(ToMove);

        }








        [MethodImpl(Inline)]
        public void SetState()
        {
            State->Checkers = bb.AttackersTo(State->KingSquares[ToMove], bb.Occupancy) & bb.Colors[Not(ToMove)];

            SetCheckInfo();

            State->Hash = Zobrist.GetHash(this);
        }


        [MethodImpl(Inline)]
        public void SetCheckInfo()
        {
            State->BlockingPieces[White] = bb.BlockingPieces(White, &State->Pinners[Black], &State->Xrays[Black]);
            State->BlockingPieces[Black] = bb.BlockingPieces(Black, &State->Pinners[White], &State->Xrays[White]);

            int kingSq = State->KingSquares[Not(ToMove)];

            State->CheckSquares[Pawn]   = PawnAttackMasks[Not(ToMove)][kingSq];
            State->CheckSquares[Knight] = KnightMasks[kingSq];
            State->CheckSquares[Bishop] = GetBishopMoves(bb.Occupancy, kingSq);
            State->CheckSquares[Rook]   = GetRookMoves(bb.Occupancy, kingSq);
            State->CheckSquares[Queen]  = (State->CheckSquares[Bishop] | State->CheckSquares[Rook]);
        }



        /// <summary>
        /// Returns the Zobrist hash of the position after the move <paramref name="m"/> is made.
        /// <para></para>
        /// This is only for simple moves and captures: en passant and castling is not considered. 
        /// This is only used for prefetching the <see cref="TTCluster"/>, and if the move actually 
        /// is an en passant or castle then the prefetch won't end up helping anyways.
        /// </summary>
        [MethodImpl(Inline)]
        public ulong HashAfter(Move m)
        {
            ulong hash = Hash.ZobristChangeToMove();
            int from = m.From;
            int to = m.To;
            int us = bb.GetColorAtIndex(from);
            int ourPiece = bb.GetPieceAtIndex(from);

            if (m.Capture)
            {
                if (EnableAssertions)
                {
                    Assert(bb.GetPieceAtIndex(to) != None,
                        "HashAfter(" + m + ") in FEN '" + GetFEN() + "' is a capture move, " +
                        "but there isn't a piece on " + IndexToString(to) + " to be captured!");
                }
                hash = hash.ZobristToggleSquare(Not(us), bb.GetPieceAtIndex(to), to);
            }

            if (EnableAssertions)
            {
                Assert(ourPiece != None,
                    "HashAfter(" + m + ") in FEN '" + GetFEN() + "' doesn't have a piece on its From square!");
            }

            hash = hash.ZobristMove(from, to, us, ourPiece);

            return hash;
        }


        /// <summary>
        /// Returns true if the move <paramref name="move"/> is pseudo-legal.
        /// Only determines if there is a piece at move.From and the piece at move.To isn't the same color.
        /// </summary>
        [MethodImpl(Inline)]
        public bool IsPseudoLegal(in Move move)
        {
            int moveTo = move.To;
            int moveFrom = move.From;

            int pt = bb.GetPieceAtIndex(moveFrom);
            if (pt == None)
            {
                //  There isn't a piece on the move's "from" square.
                return false;
            }

            int pc = bb.GetColorAtIndex(moveFrom);
            if (pc != ToMove)
            {
                return false;
            }

            if (bb.GetPieceAtIndex(moveTo) != None && (move.Capture == false || (pc == bb.GetColorAtIndex(moveTo))))
            {
                //  There is a piece on the square we are moving to, but this move wasn't generated as a capture
                //  or the piece on the To square is the same color as the one that is moving.
                return false;
            }

            if (pt == Pawn)
            {
                if (move.EnPassant)
                {
                    return (State->EPSquare != EPNone && (SquareBB[moveTo - ShiftUpDir(ToMove)] & bb.Pieces[Pawn] & bb.Colors[Not(ToMove)]) != 0);
                }

                ulong empty = ~bb.Occupancy;
                if ((moveTo ^ moveFrom) != 16)
                {
                    //  This is NOT a pawn double move, so it can only go to a square it attacks or the empty square directly above/below.
                    return ((bb.AttackMask(moveFrom, bb.GetColorAtIndex(moveFrom), pt, bb.Occupancy) & SquareBB[moveTo]) != 0 
                        || (empty & SquareBB[moveTo]) != 0);
                }
                else
                {
                    //  This IS a pawn double move, so it can only go to a square it attacks,
                    //  or the empty square 2 ranks above/below provided the square 1 rank above/below is also empty.
                    return ((bb.AttackMask(moveFrom, bb.GetColorAtIndex(moveFrom), pt, bb.Occupancy) & SquareBB[moveTo]) != 0 
                        || ((empty & SquareBB[moveTo - ShiftUpDir(pc)]) != 0 && (empty & SquareBB[moveTo]) != 0));
                }

            }

            //  This move is only pseudo-legal if the piece that is moving is actually able to get there.
            //  Pieces can only move to squares that they attack, with the one exception of queenside castling
            return ((bb.AttackMask(moveFrom, bb.GetColorAtIndex(moveFrom), pt, bb.Occupancy) & SquareBB[moveTo]) != 0 || move.Castle);
        }


        /// <summary>
        /// Returns true if the move <paramref name="move"/> is legal in the current position.
        /// </summary>
        [MethodImpl(Inline)]
        public bool IsLegal(in Move move) => IsLegal(move, State->KingSquares[ToMove], State->KingSquares[Not(ToMove)], State->BlockingPieces[ToMove]);

        /// <summary>
        /// Returns true if the move <paramref name="move"/> is legal given the current position.
        /// </summary>
        [MethodImpl(Inline)]
        public bool IsLegal(Move move, int ourKing, int theirKing, ulong pinnedPieces)
        {
            int moveFrom = move.From;
            int moveTo = move.To;

            int pt = bb.GetPieceAtIndex(moveFrom);

            if (pt == None)
            {
                return false;
            }

            if (CheckInfo.InDoubleCheck && pt != Piece.King)
            {
                //	Must move king out of double check
                return false;
            }

            if (EnableAssertions)
            {
                Assert(move.Capture == false || bb.GetPieceAtIndex(moveTo) != Piece.None,
                    "IsLegal(" + move.ToString() + " = " + move.ToString(this) + ") is trying to capture a piece on an empty square!");
            }

            int ourColor = bb.GetColorAtIndex(moveFrom);
            int theirColor = Not(ourColor);

            if (CheckInfo.InCheck)
            {
                //  We have 3 Options: block the check, take the piece giving check, or move our king out of it.

                if (pt == Piece.King)
                {
                    //  We need to move to a square that they don't attack.
                    //  We also need to consider (NeighborsMask[moveTo] & SquareBB[theirKing]), because bb.AttackersTo does NOT include king attacks
                    //  and we can't move to a square that their king attacks.
                    return ((bb.AttackersTo(moveTo, bb.Occupancy ^ SquareBB[moveFrom]) & bb.Colors[theirColor]) | (NeighborsMask[moveTo] & SquareBB[theirKing])) == 0;
                }

                int checker = CheckInfo.idxChecker;
                bool blocksOrCaptures = (LineBB[ourKing][checker] & SquareBB[moveTo]) != 0;

                if (blocksOrCaptures || (move.EnPassant && GetIndexFile(moveTo) == GetIndexFile(CheckInfo.idxChecker)))
                {
                    //  This move is another piece which has moved into the LineBB between our king and the checking piece.
                    //  This will be legal as long as it isn't pinned.

                    return (pinnedPieces == 0 || (pinnedPieces & SquareBB[moveFrom]) == 0);
                }

                //  This isn't a king move and doesn't get us out of check, so it's illegal.
                return false;
            }

            if (pt == Piece.King)
            {
                if (move.Castle)
                {
                    int rookSquare = moveTo switch
                    {
                        C1 => A1,
                        G1 => H1,
                        C8 => A8,
                        G8 => H8,
                        _ => moveFrom,
                    };

                    if (EnableAssertions)
                    {
                        Assert(rookSquare != moveFrom, "IsLegal(" + move + ") is a castle, but the To square wasn't C1/G1 or C8/G8!");
                    }

                    if ((SquareBB[rookSquare] & bb.Pieces[Rook] & bb.Colors[ourColor]) == 0)
                    {
                        return false;
                    }
                }

                //  We can move anywhere as long as it isn't attacked by them.

                //  SquareBB[ourKing] is masked out from bb.Occupancy to prevent kings from being able to move backwards out of check,
                //  meaning a king on B1 in check from a rook on C1 can't actually go to A1.
                return ((bb.AttackersTo(moveTo, (bb.Occupancy ^ SquareBB[ourKing])) & bb.Colors[theirColor]) 
                       | (NeighborsMask[moveTo] & SquareBB[theirKing])) == 0;
            }
            else if (move.EnPassant)
            {
                //  En passant will remove both our pawn and the opponents pawn from the rank so this needs a special check
                //  to make sure it is still legal

                int idxPawn = moveTo - ShiftUpDir(ourColor);
                ulong moveMask = (SquareBB[moveFrom] | SquareBB[moveTo]);

                //  This is only legal if our king is NOT attacked after the EP is made
                return (bb.AttackersTo(ourKing, (bb.Occupancy ^ (moveMask | SquareBB[idxPawn]))) & bb.Colors[Not(ourColor)]) == 0;
            }

            //  Otherwise, this move is legal if:
            //  The piece we are moving isn't a blocker for our king
            //  The piece is a blocker for our king, but it is moving along the same ray that it had been blocking previously.
            //  (i.e. a rook on B1 moving to A1 to capture a rook that was pinning it to our king on C1)
            return ((State->BlockingPieces[ourColor] & SquareBB[moveFrom]) == 0) ||
                   ((RayBB[moveFrom][moveTo] & SquareBB[ourKing]) != 0);
        }




        [MethodImpl(Inline)]
        public bool IsDraw()
        {
            return (IsFiftyMoveDraw() || IsInsufficientMaterial() || IsThreefoldRepetition());
        }


        /// <summary>
        /// Checks if the position is currently drawn by insufficient material.
        /// This generally only happens for KvK, KvKB, and KvKN endgames.
        /// </summary>
        [MethodImpl(Inline)]
        public bool IsInsufficientMaterial()
        {
            if ((bb.Pieces[Piece.Queen] | bb.Pieces[Piece.Rook] | bb.Pieces[Piece.Pawn]) != 0)
            {
                return false;
            }

            ulong knights = popcount(bb.Pieces[Piece.Knight]);
            ulong bishops = popcount(bb.Pieces[Piece.Bishop]);

            //  Just kings, only 1 bishop, or 1 or 2 knights is a draw
            //  Some organizations classify 2 knights a draw and others don't.
            return ((knights == 0 && bishops < 2) || (bishops == 0 && knights <= 2));
        }


        /// <summary>
        /// Checks if the position is currently drawn by threefold repetition.
        /// Only considers moves made past the last time the HalfMoves clock was reset,
        /// which occurs when captures are made or a pawn moves.
        /// </summary>
        [MethodImpl(Inline)]
        public bool IsThreefoldRepetition()
        {
            //  At least 8 moves must be made before a draw can occur.
            if (GamePly < LowestRepetitionCount)
            {
                return false;
            }
            
            ulong currHash = State->Hash;

            //  Beginning with the current state's Hash, step backwards in increments of 2 until reaching the first move that we made.
            //  If we encounter the current hash 2 additional times, then this is a draw.

            int count = 0;
            StateInfo* temp = State;
            for (int i = 0; i < GamePly - 1; i += 2)
            {
                if (temp->Hash == currHash)
                {
                    count++;

                    if (count == 3)
                    {
                        return true;
                    }
                }

                if ((temp - 1) == _SentinelStart || (temp - 2) == _SentinelStart) {
                    break;
                }

                temp -= 2;
            }
            return false;
        }

        [MethodImpl(Inline)]
        public bool IsFiftyMoveDraw()
        {
            return State->HalfmoveClock >= 100;
        }



        /// <summary>
        /// Returns the number of leaf nodes in the current position up to <paramref name="depth"/>.
        /// </summary>
        /// <param name="depth">The number of moves that will be made from the starting position. Depth 1 returns the current number of legal moves.</param>
        [MethodImpl(Inline)]
        public ulong Perft(int depth)
        {
            ScoredMove* list = stackalloc ScoredMove[MoveListSize];
            int size = GenLegal(list);

            if (depth == 1)
            {
                return (ulong)size;
            }

            ulong n = 0;
            for (int i = 0; i < size; i++)
            {
                Move m = list[i].Move;
                MakeMove(m);
                n += Perft(depth - 1);
                UnmakeMove(m);
            }
            return n;
        }

        private Stopwatch PerftTimer = new Stopwatch();
        private const int PerftParallelMinDepth = 6;
        [MethodImpl(Inline)]
        public ulong PerftParallel(int depth, bool isRoot = false)
        {
            if (isRoot)
            {
                PerftTimer.Restart();
            }

            //  This needs to be a pointer since a Span is a ref local and they can't be used inside of lambda functions.
            ScoredMove* list = stackalloc ScoredMove[MoveListSize];
            int size = GenLegal(list);

            ulong n = 0;

            string rootFEN = GetFEN();

            ParallelOptions opts = new ParallelOptions();
            opts.MaxDegreeOfParallelism = MoveListSize;
            Parallel.For(0u, size, opts, i =>
            {
                Position threadPosition = new Position(rootFEN, false, owner: SearchPool.MainThread);

                threadPosition.MakeMove(list[i].Move);
                ulong result = (depth >= PerftParallelMinDepth) ? threadPosition.PerftParallel(depth - 1) : threadPosition.Perft(depth - 1);
                if (isRoot)
                {
                    Log(list[i].Move.ToString() + ": " + result);
                }
                n += result;
            });

            if (isRoot)
            {
                PerftTimer.Stop();
                Log("\r\nNodes searched:  " + n + " in " + PerftTimer.Elapsed.TotalSeconds + " s (" + ((int)(n / PerftTimer.Elapsed.TotalSeconds)).ToString("N0") + " nps)" + "\r\n");
                PerftTimer.Reset();
            }

            return n;
        }

        /// <summary>
        /// Same as perft but returns the evaluation at each of the leaves. 
        /// Only for benchmarking/debugging.
        /// </summary>
        [MethodImpl(Inline)]
        public ulong PerftNN(int depth)
        {
            ScoredMove* list = stackalloc ScoredMove[MoveListSize];
            int size = GenLegal(list);

            if (depth == 0)
            {
                return (ulong)HalfKA_HM.GetEvaluation(this);
            }

            ulong n = 0;
            for (int i = 0; i < size; i++)
            {
                Move m = list[i].Move;
                MakeMove(m);
                n += PerftNN(depth - 1);
                UnmakeMove(m);
            }

            return n;
        }



        /// <summary>
        /// Updates the position's Bitboard, ToMove, castling status, en passant target, and half/full move clock.
        /// </summary>
        /// <param name="fen">The FEN to set the position to</param>
        public bool LoadFromFEN(string fen)
        {
            try
            {
                string[] splits = fen.Split(new char[] { '/', ' ' });

                bb.Reset();
                FullMoves = 1;

                State = StartingState;
                NativeMemory.Clear(State, StateInfo.StateCopySize);
                State->CastleStatus = CastlingStatus.None;
                State->HalfmoveClock = 0;

                //  TODO: set GamePly to 0 here?

                for (int i = 0; i < splits.Length; i++)
                {
                    //	it's a row on the board
                    if (i <= 7)
                    {
                        int pieceX = 0;
                        for (int x = 0; x < splits[i].Length; x++)
                        {
                            if (char.IsLetter(splits[i][x]))
                            {
                                int idx = CoordToIndex(pieceX, 7 - i);
                                int pc = (char.IsUpper(splits[i][x]) ? White : Black);
                                int pt = FENToPiece(splits[i][x]);

                                bb.AddPiece(idx, pc, pt);
                                
                                pieceX++;
                            }
                            else if (char.IsDigit(splits[i][x]))
                            {
                                int add = int.Parse(splits[i][x].ToString());
                                pieceX += add;
                            }
                            else
                            {
                                Log("ERROR x for i = " + i + " was '" + splits[i][x] + "' and didn't get parsed");
                            }
                        }
                    }
                    //	who moves next
                    else if (i == 8)
                    {
                        ToMove = splits[i].Equals("w") ? Color.White : Color.Black;
                    }
                    //	castling availability
                    else if (i == 9)
                    {
                        if (splits[i].Contains('-'))
                        {
                            State->CastleStatus = CastlingStatus.None;
                        }
                        else
                        {
                            State->CastleStatus |= splits[i].Contains('K') ? CastlingStatus.WK : 0;
                            State->CastleStatus |= splits[i].Contains('Q') ? CastlingStatus.WQ : 0;
                            State->CastleStatus |= splits[i].Contains('k') ? CastlingStatus.BK : 0;
                            State->CastleStatus |= splits[i].Contains('q') ? CastlingStatus.BQ : 0;
                        }
                    }
                    //	en passant target or last double pawn move
                    else if (i == 10)
                    {
                        if (!splits[i].Contains('-'))
                        {
                            //	White moved a pawn last
                            if (splits[i][1].Equals('3'))
                            {
                                State->EPSquare = StringToIndex(splits[i]);
                            }
                            else if (splits[i][1].Equals('6'))
                            {
                                State->EPSquare = StringToIndex(splits[i]);
                            }
                        }
                    }
                    //	halfmove number
                    else if (i == 11)
                    {
                        if (int.TryParse(splits[i], out int halfMoves))
                        {
                            State->HalfmoveClock = halfMoves;
                        }
                    }
                    //	fullmove number
                    else if (i == 12)
                    {
                        if (int.TryParse(splits[i], out int fullMoves))
                        {
                            FullMoves = fullMoves;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Failed parsing '" + fen + "': ");
                Log(ex.ToString());

                return false;
            }

            State->KingSquares[White] = bb.KingIndex(White);
            State->KingSquares[Black] = bb.KingIndex(Black);

            this.bb.DetermineCheck(ToMove, ref CheckInfo);
            Hash = Zobrist.GetHash(this);

            SetState();

            State->CapturedPiece = None;

            MaterialCount[Color.White] = bb.MaterialCount(Color.White);
            MaterialCount[Color.Black] = bb.MaterialCount(Color.Black);

            MaterialCountNonPawn[White] = MaterialCount[White] - (GetPieceValue(Pawn) * (int)popcount(bb.Colors[White] & bb.Pieces[Pawn]));
            MaterialCountNonPawn[Black] = MaterialCount[Black] - (GetPieceValue(Pawn) * (int)popcount(bb.Colors[Black] & bb.Pieces[Pawn]));

            if (UpdateNN)
            {
                State->Accumulator->RefreshPerspective[White] = true;
                State->Accumulator->RefreshPerspective[Black] = true;
            }

            if (UseHalfKA && popcount(bb.Occupancy) > HalfKA_HM.MaxActiveDimensions)
            {
                throw new IndexOutOfRangeException("ERROR FEN '" + fen + "' has more than " + HalfKA_HM.MaxActiveDimensions + " pieces, which isn't allowed with the HalfKA architecture!");
            }

            return true;
        }

        /// <summary>
        /// Returns the FEN string of the current position.
        /// </summary>
        public string GetFEN()
        {
            StringBuilder fen = new StringBuilder();

            for (int y = 7; y >= 0; y--)
            {
                int i = 0;
                for (int x = 0; x <= 7; x++)
                {
                    int index = CoordToIndex(x, y);
                    if (bb.IsColorSet(Color.White, index))
                    {
                        if (i != 0)
                        {
                            fen.Append(i);
                            i = 0;
                        }

                        int pt = bb.GetPieceAtIndex(index);
                        if (pt != Piece.None)
                        {
                            char c = PieceToFENChar(pt);
                            fen.Append(char.ToUpper(c));
                        }
                        else
                        {
                            Log("WARN in GetFEN(), White's color is set for " + IndexToString(index) + ", but there isn't a piece on that square!");
                        }

                        continue;
                    }
                    else if (bb.IsColorSet(Color.Black, index))
                    {
                        if (i != 0)
                        {
                            fen.Append(i);
                            i = 0;
                        }

                        int pt = bb.GetPieceAtIndex(index);
                        if (pt != Piece.None)
                        {
                            char c = PieceToFENChar(pt);
                            fen.Append(char.ToLower(c));
                        }
                        else
                        {
                            Log("WARN in GetFEN(), Black's color is set for " + IndexToString(index) + ", but there isn't a piece on that square!");
                        }


                        continue;
                    }
                    else
                    {
                        i++;
                    }
                    if (x == 7)
                    {
                        fen.Append(i);
                    }
                }
                if (y != 0)
                {
                    fen.Append('/');
                }
            }

            fen.Append(ToMove == Color.White ? " w " : " b ");

            bool CanC = false;
            if (State->CastleStatus.HasFlag(CastlingStatus.WK))
            {
                fen.Append('K');
                CanC = true;
            }
            if (State->CastleStatus.HasFlag(CastlingStatus.WQ))
            {
                fen.Append('Q');
                CanC = true;
            }
            if (State->CastleStatus.HasFlag(CastlingStatus.BK))
            {
                fen.Append('k');
                CanC = true;
            }
            if (State->CastleStatus.HasFlag(CastlingStatus.BQ))
            {
                fen.Append('q');
                CanC = true;
            }
            if (!CanC)
            {
                fen.Append('-');
            }
            if (State->EPSquare != EPNone)
            {
                fen.Append(" " + IndexToString(State->EPSquare));
            }
            else
            {
                fen.Append(" -");
            }

            fen.Append(" " + State->HalfmoveClock + " " + FullMoves);

            return fen.ToString();
        }

        public override string ToString()
        {
            return PrintBoard(bb);
        }

    }
}
