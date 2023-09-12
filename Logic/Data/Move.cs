﻿using System.Runtime.InteropServices;
using System.Text;

namespace LTChess.Logic.Data
{
    [StructLayout(LayoutKind.Auto)]
    public unsafe struct Move
    {
        public static readonly Move Null = new Move();

        //  6 bits for: From, To, SqChecker, and SqDoubleChecker
        //  
        //  2 bits for PromotionTo, which defaults to a knight (1), so the "Promotion" flag MUST be looked at before "PromotionTo" is.
        //  (Otherwise every move would show up as a promotion to a knight, woohoo for horses!).
        //  
        //  6 bits for the 6 move flags.
        //  
        //  Total of 32.
        private int _data;

        private const int FlagCapture =     0b000001 << 26;
        private const int FlagEnPassant =   0b000010 << 26;
        private const int FlagCastle =      0b000100 << 26;
        private const int FlagCheck =       0b001000 << 26;
        private const int FlagDoubleCheck = 0b010000 << 26;
        private const int FlagPromotion =   0b100000 << 26;

        private const int Mask_Check = 0b011000 << 26;
        private const int Mask_ToFrom = 0xFFF;


        /// <summary>
        /// Gets or sets the square that this piece is moving to.
        /// </summary>
        public int To
        {
            //  Reminder to future self: This is a property, and calling move.get_To() 157,582,869 times at depth 15 is a no-no.
            get => (_data & 0x3F);
            set => _data = ((_data & ~0x3F) | value);
        }

        /// <summary>
        /// Gets or sets the square that this piece is moving from.
        /// </summary>
        public int From
        {
            //  Reminder to future self: This is a property, and calling move.get_From() 124,310,980 times at depth 15 is a no-no.
            get => ((_data >> 6) & 0x3F);
            set => _data = ((_data & ~(0x3F << 6)) | (value << 6));
        }

        /// <summary>
        /// Gets a mask of this move's To and From, with the From data still remaining left shifted by 6.
        /// Only for use in History/Capture heuristic tables.
        /// </summary>
        public int MoveMask
        {
            get => (_data & Mask_ToFrom);
        }

        /// <summary>
        /// Gets or sets the square that this move causes check from.
        /// </summary>
        public int SqChecker
        {
            get => ((_data >> 12) & 0x3F);
            set => _data = ((_data & ~(0x3F << 12)) | (value << 12));
        }

        /// <summary>
        /// Gets or sets the square that this move causes check from.
        /// <para></para>
        /// This is currently unused since SqChecker is only used to determine if a non-king move captures the piece giving check (or blocks it),
        /// and double checks can't be resolved by a non-king move.
        /// </summary>
        public int SqDoubleChecker
        {
            get => ((_data >> 18) & 0x3F);
            set => _data = ((_data & ~(0x3F << 18)) | (value << 18));
        }

        /// <summary>
        /// Gets or sets the piece type that this pawn is promoting to. This is stored as (piece type - 1) to save space,
        /// so a PromotionTo == 0 (Piece.Pawn) is treated as 1 (Piece.Knight).
        /// </summary>
        public int PromotionTo
        {
            get => ((_data >> 24) & 0x3) + 1;
            set => _data = ((_data & ~(0x3 << 24)) | ((value - 1) << 24));
        }

        /// <summary>
        /// Gets or sets whether this move is a capture or not.
        /// </summary>
        public bool Capture
        {
            get => ((_data & FlagCapture) != 0);
            set => _data ^= FlagCapture;
        }

        /// <summary>
        /// Gets or sets whether this pawn move is an en passant or not.
        /// </summary>
        public bool EnPassant
        {
            get => ((_data & FlagEnPassant) != 0);
            set => _data ^= FlagEnPassant;
        }

        /// <summary>
        /// Gets or sets whether this king move is a castling one or not.
        /// </summary>
        public bool Castle
        {
            get => ((_data & FlagCastle) != 0);
            set => _data ^= FlagCastle;
        }

        /// <summary>
        /// Gets or sets whether this move puts the other player's king in check.
        /// </summary>
        public bool CausesCheck
        {
            get => ((_data & FlagCheck) != 0);
            set => _data ^= FlagCheck;
        }

        /// <summary>
        /// Gets or sets whether this move puts the other player's king in check from two pieces.
        /// </summary>
        public bool CausesDoubleCheck
        {
            get => ((_data & FlagDoubleCheck) != 0);
            set => _data ^= FlagDoubleCheck;
        }

        /// <summary>
        /// Gets or sets whether this pawn move is a promotion.
        /// </summary>
        public bool Promotion
        {
            get => ((_data & FlagPromotion) != 0);
            set => _data ^= FlagPromotion;
        }

        /// <summary>
        /// Returns true if this move causes check or double check.
        /// </summary>
        public bool Checks => ((_data & Mask_Check) != 0);


        public Move(int from, int to)
        {
            _data |= (to);
            _data |= (from << 6);
        }

        public Move(int from, int to, int promotionTo)
        {
            _data |= (to);
            _data |= (from << 6);
            this.PromotionTo = promotionTo;
            Promotion = true;
        }


        [MethodImpl(Inline)]
        public bool IsNull()
        {
            return (From == 0 && To == 0 && SqChecker == 0);
        }

        [MethodImpl(Inline)]
        public ulong GetMoveMask()
        {
            return (SquareBB[From] | SquareBB[To]);
        }

        /// <summary>
        /// Returns the generic string representation of a move, which is just the move's From square, the To square,
        /// and the piece that the move is promoting to if applicable.
        /// <br></br>
        /// For example, the opening moves "e4 e5, Nf3 Nc6, ..." would be "e2e4 e7e5, g1f3 b8c6, ..."
        /// </summary>
        [MethodImpl(Inline)]
        public string SmithNotation()
        {
            IndexToCoord(From, out int fx, out int fy);
            IndexToCoord(To, out int tx, out int ty);

            if (Promotion)
            {
                return "" + GetFileChar(fx) + (fy + 1) + GetFileChar(tx) + (ty + 1) + char.ToLower(PieceToFENChar(PromotionTo));
            }
            else
            {
                return "" + GetFileChar(fx) + (fy + 1) + GetFileChar(tx) + (ty + 1);
            }
        }

        [MethodImpl(Inline)]
        public string ToString(Position position)
        {
            StringBuilder sb = new StringBuilder();
            ref Bitboard bb = ref position.bb;

            int moveTo = To;
            int moveFrom = From;

            int pt = bb.PieceTypes[moveFrom];

            if (Castle)
            {
                if (moveTo > moveFrom)
                {
                    sb.Append("O-O");
                }
                else
                {
                    sb.Append("O-O-O");
                }
            }
            else
            {
                bool cap = bb.Occupied(moveTo);

                if (pt == Piece.Pawn)
                {
                    if (cap || EnPassant)
                    {
                        sb.Append(GetFileChar(GetIndexFile(moveFrom)));
                    }
                }
                else
                {
                    sb.Append(PieceToFENChar(pt));
                }

                //  If multiple of the same piece type can move to the same square, then we have to
                //  differentiate them by including either the file, rank, or both, that this piece is moving from.
                ulong multPieces = bb.AttackersTo(moveTo, bb.Occupancy) & bb.Colors[bb.GetColorAtIndex(moveFrom)] & bb.Pieces[pt];
                if (popcount(multPieces) > 1)
                {
                    if ((multPieces & GetFileBB(moveFrom)) == SquareBB[moveFrom])
                    {
                        //  If this piece is alone on its file, we only specify the file.
                        sb.Append(GetFileChar(GetIndexFile(moveFrom)));
                    }
                    else if ((multPieces & GetRankBB(moveFrom)) == SquareBB[moveFrom])
                    {
                        //  If this piece wasn't alone on its file, but is alone on its rank, then include the rank.
                        sb.Append(GetIndexRank(moveFrom) + 1);
                    }
                    else
                    {
                        //  If neither the rank/file alone could differentiate this move, then we need both the file and rank
                        sb.Append(GetFileChar(GetIndexFile(moveFrom)));
                        sb.Append(GetIndexRank(moveFrom) + 1);
                    }
                }

                if (cap || EnPassant)
                {
                    sb.Append('x');
                }


                sb.Append(IndexToString(moveTo));

                if (Promotion)
                {
                    sb.Append("=" + PieceToFENChar(PromotionTo));
                }
            }

            if (CausesCheck || CausesDoubleCheck)
            {
                sb.Append("+");
            }


            return sb.ToString();
        }

        [MethodImpl(Inline)]
        public override string ToString()
        {
            return SmithNotation();
        }

        /// <summary>
        /// Returns true if <paramref name="obj"/> is a <see cref="Move"/> or <see cref="CondensedMove"/> with the same From/To squares, the same "Castle" flag, and the same PromotionTo piece.
        /// </summary>
        [MethodImpl(Inline)]
        public override bool Equals(object? obj)
        {
            if (obj is Move move)
            {
                return (move.From == this.From && move.To == this.To && move.Castle == this.Castle && move.PromotionTo == this.PromotionTo);
            }

            if (obj is CondensedMove other)
            {
                return (other.From == this.From && other.To == this.To && other.Castle == this.Castle && other.PromotionTo == this.PromotionTo);
            }

            return false;
        }


        [MethodImpl(Inline)]
        public static bool operator ==(Move left, Move right)
        {
            return left.Equals(right);
        }

        [MethodImpl(Inline)]
        public static bool operator !=(Move left, Move right)
        {
            return !left.Equals(right);
        }
    }
}
