// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System;

namespace SixLabors.ZlibStream
{
    internal sealed class InfBlocks
    {
        // Table for deflate from PKZIP's appnote.txt.
        internal static readonly int[] Border = new int[]
        {
            16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15,
        };

        private const int MANY = 1440;
        private const int TYPE = 0; // get type bits (3, including end bit)
        private const int LENS = 1; // get lengths for stored
        private const int STORED = 2; // processing stored block
        private const int TABLE = 3; // get table lengths
        private const int BTREE = 4; // get bit lengths tree for a dynamic block
        private const int DTREE = 5; // get length, distance trees for a dynamic block
        private const int CODES = 6; // processing fixed or dynamic block
        private const int DRY = 7; // output remaining window bytes
        private const int DONE = 8; // finished last block, done
        private const int BAD = 9; // ot a data error--stuck here

        // And'ing with mask[n] masks the lower n bits
        private static readonly int[] InflateMask = new int[]
        {
            0x00000000, 0x00000001, 0x00000003, 0x00000007, 0x0000000f, 0x0000001f, 0x0000003f,
            0x0000007f, 0x000000ff, 0x000001ff, 0x000003ff, 0x000007ff, 0x00000fff, 0x00001fff,
            0x00003fff, 0x00007fff, 0x0000ffff,
        };

        private readonly int[] bb = new int[1]; // bit length tree depth
        private readonly int[] tb = new int[1]; // bit length decoding tree
        private readonly bool doCheck; // check function

        private int mode; // current inflate_block mode
        private int left; // if STORED, bytes left to copy
        private int table; // table lengths (14 bits)
        private int index; // index into blens (or border)
        private int[] blens; // bit lengths of codes
        private InfCodes codes; // if CODES, current state
        private int last; // true if this block is the last block
        private int[] hufts; // single malloc for tree space
        private uint check; // check on output

        /// <summary>
        /// Initializes a new instance of the <see cref="InfBlocks"/> class.
        /// </summary>
        /// <param name="zStream">Zlib Stream.</param>
        /// <param name="doCheck">Whether to calculate the checksum.</param>
        /// <param name="windowSize">Window size.</param>
        internal InfBlocks(ZStream zStream, bool doCheck, int windowSize)
        {
            // TODO: Pool.
            this.hufts = new int[MANY * 3];
            this.Window = new byte[windowSize];
            this.End = windowSize;
            this.doCheck = doCheck;
            this.mode = TYPE;
            this.Reset(zStream, null);
        }

        internal int End { get; private set; } // one byte after sliding window

        internal int Bitk { get; set; } // bits in bit buffer

        internal int Bitb { get; set; } // bit buffer

        internal byte[] Window { get; private set; } // sliding window

        internal int Read { get; private set; } // window read pointer

        internal int Write { get; set; } // window write pointer

        internal void Reset(ZStream zStream, long[] c)
        {
            if (c != null)
            {
                c[0] = this.check;
            }

            if (this.mode == BTREE || this.mode == DTREE)
            {
                this.blens = null;
            }

            if (this.mode == CODES)
            {
                InfCodes.Free();
            }

            this.mode = TYPE;
            this.Bitk = 0;
            this.Bitb = 0;
            this.Read = this.Write = 0;

            if (this.doCheck)
            {
                zStream.Adler = this.check = Adler32.SeedValue;
            }
        }

        internal CompressionState Proc(ZStream z, CompressionState r)
        {
            int t; // temporary storage
            int b; // bit buffer
            int k; // bits in bit buffer
            int p; // input data pointer
            int n; // bytes available there
            int q; // output window write pointer
            int m; // bytes to end of window or read pointer

            // copy input/output information to locals (UPDATE macro restores)
            {
                p = z.NextInIndex;
                n = z.AvailableIn;
                b = this.Bitb;
                k = this.Bitk;
            }

            {
                q = this.Write;
                m = q < this.Read ? this.Read - q - 1 : this.End - q;
            }

            // process input based on current state
            while (true)
            {
                switch (this.mode)
                {
                    case TYPE:

                        while (k < 3)
                        {
                            if (n != 0)
                            {
                                r = CompressionState.ZOK;
                            }
                            else
                            {
                                this.Bitb = b;
                                this.Bitk = k;
                                z.AvailableIn = n;
                                z.TotalIn += p - z.NextInIndex;
                                z.NextInIndex = p;
                                this.Write = q;
                                return this.Inflate_flush(z, r);
                            }

                            n--;
                            b |= (z.NextIn[p++] & 0xff) << k;
                            k += 8;
                        }

                        t = b & 7;
                        this.last = t & 1;

                        switch (t >> 1)
                        {
                            case 0: // stored
                            {
                                b >>= 3;
                                k -= 3;
                            }

                            t = k & 7; // go to byte boundary
                            {
                                b >>= t;
                                k -= t;
                            }

                            this.mode = LENS; // get length of stored block
                            break;

                            case 1: // fixed
                            {
                                var bl = new int[1];
                                var bd = new int[1];
                                var tl = new int[1][];
                                var td = new int[1][];

                                _ = InfTree.Inflate_trees_fixed(bl, bd, tl, td);
                                this.codes = new InfCodes(bl[0], bd[0], tl[0], td[0]);
                            }

                            {
                                b >>= 3;
                                k -= 3;
                            }

                            this.mode = CODES;
                            break;

                            case 2: // dynamic
                            {
                                b >>= 3;
                                k -= 3;
                            }

                            this.mode = TABLE;
                            break;

                            case 3: // illegal
                            {
                                b >>= 3;
                                k -= 3;
                            }

                            this.mode = BAD;
                            z.Message = "invalid block type";
                            r = CompressionState.ZDATAERROR;

                            this.Bitb = b;
                            this.Bitk = k;
                            z.AvailableIn = n;
                            z.TotalIn += p - z.NextInIndex;
                            z.NextInIndex = p;
                            this.Write = q;
                            return this.Inflate_flush(z, r);
                        }

                        break;

                    case LENS:

                        while (k < 32)
                        {
                            if (n != 0)
                            {
                                r = CompressionState.ZOK;
                            }
                            else
                            {
                                this.Bitb = b;
                                this.Bitk = k;
                                z.AvailableIn = n;
                                z.TotalIn += p - z.NextInIndex;
                                z.NextInIndex = p;
                                this.Write = q;
                                return this.Inflate_flush(z, r);
                            }

                            n--;
                            b |= (z.NextIn[p++] & 0xff) << k;
                            k += 8;
                        }

                        if (((~b >> 16) & 0xffff) != (b & 0xffff))
                        {
                            this.mode = BAD;
                            z.Message = "invalid stored block lengths";
                            r = CompressionState.ZDATAERROR;

                            this.Bitb = b;
                            this.Bitk = k;
                            z.AvailableIn = n;
                            z.TotalIn += p - z.NextInIndex;
                            z.NextInIndex = p;
                            this.Write = q;
                            return this.Inflate_flush(z, r);
                        }

                        this.left = b & 0xffff;
                        b = k = 0; // dump bits
                        this.mode = this.left != 0 ? STORED : (this.last != 0 ? DRY : TYPE);
                        break;

                    case STORED:
                        if (n == 0)
                        {
                            this.Bitb = b;
                            this.Bitk = k;
                            z.AvailableIn = n;
                            z.TotalIn += p - z.NextInIndex;
                            z.NextInIndex = p;
                            this.Write = q;
                            return this.Inflate_flush(z, r);
                        }

                        if (m == 0)
                        {
                            if (q == this.End && this.Read != 0)
                            {
                                q = 0;
                                m = q < this.Read ? this.Read - q - 1 : this.End - q;
                            }

                            if (m == 0)
                            {
                                this.Write = q;
                                r = this.Inflate_flush(z, r);
                                q = this.Write;
                                m = q < this.Read ? this.Read - q - 1 : this.End - q;
                                if (q == this.End && this.Read != 0)
                                {
                                    q = 0;
                                    m = q < this.Read ? this.Read - q - 1 : this.End - q;
                                }

                                if (m == 0)
                                {
                                    this.Bitb = b;
                                    this.Bitk = k;
                                    z.AvailableIn = n;
                                    z.TotalIn += p - z.NextInIndex;
                                    z.NextInIndex = p;
                                    this.Write = q;
                                    return this.Inflate_flush(z, r);
                                }
                            }
                        }

                        r = CompressionState.ZOK;

                        t = this.left;
                        if (t > n)
                        {
                            t = n;
                        }

                        if (t > m)
                        {
                            t = m;
                        }

                        Buffer.BlockCopy(z.NextIn, p, this.Window, q, t);
                        p += t;
                        n -= t;
                        q += t;
                        m -= t;
                        if ((this.left -= t) != 0)
                        {
                            break;
                        }

                        this.mode = this.last != 0 ? DRY : TYPE;
                        break;

                    case TABLE:

                        while (k < 14)
                        {
                            if (n != 0)
                            {
                                r = CompressionState.ZOK;
                            }
                            else
                            {
                                this.Bitb = b;
                                this.Bitk = k;
                                z.AvailableIn = n;
                                z.TotalIn += p - z.NextInIndex;
                                z.NextInIndex = p;
                                this.Write = q;
                                return this.Inflate_flush(z, r);
                            }

                            n--;
                            b |= (z.NextIn[p++] & 0xff) << k;
                            k += 8;
                        }

                        this.table = t = b & 0x3fff;
                        if ((t & 0x1f) > 29 || ((t >> 5) & 0x1f) > 29)
                        {
                            this.mode = BAD;
                            z.Message = "too many length or distance symbols";
                            r = CompressionState.ZDATAERROR;

                            this.Bitb = b;
                            this.Bitk = k;
                            z.AvailableIn = n;
                            z.TotalIn += p - z.NextInIndex;
                            z.NextInIndex = p;
                            this.Write = q;
                            return this.Inflate_flush(z, r);
                        }

                        t = 258 + (t & 0x1f) + ((t >> 5) & 0x1f);
                        this.blens = new int[t];
                        {
                            b >>= 14;
                            k -= 14;
                        }

                        this.index = 0;
                        this.mode = BTREE;
                        goto case BTREE;

                    case BTREE:
                        while (this.index < 4 + (this.table >> 10))
                        {
                            while (k < 3)
                            {
                                if (n != 0)
                                {
                                    r = CompressionState.ZOK;
                                }
                                else
                                {
                                    this.Bitb = b;
                                    this.Bitk = k;
                                    z.AvailableIn = n;
                                    z.TotalIn += p - z.NextInIndex;
                                    z.NextInIndex = p;
                                    this.Write = q;
                                    return this.Inflate_flush(z, r);
                                }

                                n--;
                                b |= (z.NextIn[p++] & 0xff) << k;
                                k += 8;
                            }

                            this.blens[Border[this.index++]] = b & 7;
                            {
                                b >>= 3;
                                k -= 3;
                            }
                        }

                        while (this.index < 19)
                        {
                            this.blens[Border[this.index++]] = 0;
                        }

                        this.bb[0] = 7;
                        t = (int)InfTree.Inflate_trees_bits(this.blens, this.bb, this.tb, this.hufts, z);
                        if (t != (int)CompressionState.ZOK)
                        {
                            r = (CompressionState)t;
                            if (r == CompressionState.ZDATAERROR)
                            {
                                this.blens = null;
                                this.mode = BAD;
                            }

                            this.Bitb = b;
                            this.Bitk = k;
                            z.AvailableIn = n;
                            z.TotalIn += p - z.NextInIndex;
                            z.NextInIndex = p;
                            this.Write = q;
                            return this.Inflate_flush(z, r);
                        }

                        this.index = 0;
                        this.mode = DTREE;
                        goto case DTREE;

                    case DTREE:
                        while (true)
                        {
                            t = this.table;
                            if (!(this.index < 258 + (t & 0x1f) + ((t >> 5) & 0x1f)))
                            {
                                break;
                            }

                            int i, j, c;

                            t = this.bb[0];

                            while (k < t)
                            {
                                if (n != 0)
                                {
                                    r = CompressionState.ZOK;
                                }
                                else
                                {
                                    this.Bitb = b;
                                    this.Bitk = k;
                                    z.AvailableIn = n;
                                    z.TotalIn += p - z.NextInIndex;
                                    z.NextInIndex = p;
                                    this.Write = q;
                                    return this.Inflate_flush(z, r);
                                }

                                n--;
                                b |= (z.NextIn[p++] & 0xff) << k;
                                k += 8;
                            }

                            if (this.tb[0] == -1)
                            {
                                // System.err.println("null...");
                            }

                            t = this.hufts[((this.tb[0] + (b & InflateMask[t])) * 3) + 1];
                            c = this.hufts[((this.tb[0] + (b & InflateMask[t])) * 3) + 2];

                            if (c < 16)
                            {
                                b >>= t;
                                k -= t;
                                this.blens[this.index++] = c;
                            }
                            else
                            {
                                // c == 16..18
                                i = c == 18 ? 7 : c - 14;
                                j = c == 18 ? 11 : 3;

                                while (k < (t + i))
                                {
                                    if (n != 0)
                                    {
                                        r = CompressionState.ZOK;
                                    }
                                    else
                                    {
                                        this.Bitb = b;
                                        this.Bitk = k;
                                        z.AvailableIn = n;
                                        z.TotalIn += p - z.NextInIndex;
                                        z.NextInIndex = p;
                                        this.Write = q;
                                        return this.Inflate_flush(z, r);
                                    }

                                    n--;
                                    b |= (z.NextIn[p++] & 0xff) << k;
                                    k += 8;
                                }

                                b >>= t;
                                k -= t;

                                j += b & InflateMask[i];

                                b >>= i;
                                k -= i;

                                i = this.index;
                                t = this.table;
                                if (i + j > 258 + (t & 0x1f) + ((t >> 5) & 0x1f) || (c == 16 && i < 1))
                                {
                                    this.blens = null;
                                    this.mode = BAD;
                                    z.Message = "invalid bit length repeat";
                                    r = CompressionState.ZDATAERROR;

                                    this.Bitb = b;
                                    this.Bitk = k;
                                    z.AvailableIn = n;
                                    z.TotalIn += p - z.NextInIndex;
                                    z.NextInIndex = p;
                                    this.Write = q;
                                    return this.Inflate_flush(z, r);
                                }

                                c = c == 16 ? this.blens[i - 1] : 0;
                                do
                                {
                                    this.blens[i++] = c;
                                }
                                while (--j != 0);
                                this.index = i;
                            }
                        }

                        this.tb[0] = -1;
                        {
                            var bl = new int[1];
                            var bd = new int[1];
                            var tl = new int[1];
                            var td = new int[1];

                            bl[0] = 9; // must be <= 9 for lookahead assumptions
                            bd[0] = 6; // must be <= 9 for lookahead assumptions
                            t = this.table;
                            t = (int)InfTree.Inflate_trees_dynamic(257 + (t & 0x1f), 1 + ((t >> 5) & 0x1f), this.blens, bl, bd, tl, td, this.hufts, z);
                            if (t != (int)CompressionState.ZOK)
                            {
                                if (t == (int)CompressionState.ZDATAERROR)
                                {
                                    this.blens = null;
                                    this.mode = BAD;
                                }

                                r = (CompressionState)t;

                                this.Bitb = b;
                                this.Bitk = k;
                                z.AvailableIn = n;
                                z.TotalIn += p - z.NextInIndex;
                                z.NextInIndex = p;
                                this.Write = q;
                                return this.Inflate_flush(z, r);
                            }

                            this.codes = new InfCodes(bl[0], bd[0], this.hufts, tl[0], this.hufts, td[0]);
                        }

                        this.blens = null;
                        this.mode = CODES;
                        goto case CODES;

                    case CODES:
                        this.Bitb = b;
                        this.Bitk = k;
                        z.AvailableIn = n;
                        z.TotalIn += p - z.NextInIndex;
                        z.NextInIndex = p;
                        this.Write = q;

                        if ((r = this.codes.Proc(this, z, r)) != CompressionState.ZSTREAMEND)
                        {
                            return this.Inflate_flush(z, r);
                        }

                        r = CompressionState.ZOK;
                        InfCodes.Free();

                        p = z.NextInIndex;
                        n = z.AvailableIn;
                        b = this.Bitb;
                        k = this.Bitk;
                        q = this.Write;
                        m = q < this.Read ? this.Read - q - 1 : this.End - q;

                        if (this.last == 0)
                        {
                            this.mode = TYPE;
                            break;
                        }

                        this.mode = DRY;
                        goto case DRY;

                    case DRY:
                        this.Write = q;
                        r = this.Inflate_flush(z, r);
                        q = this.Write;
                        m = q < this.Read ? this.Read - q - 1 : this.End - q;
                        if (this.Read != this.Write)
                        {
                            this.Bitb = b;
                            this.Bitk = k;
                            z.AvailableIn = n;
                            z.TotalIn += p - z.NextInIndex;
                            z.NextInIndex = p;
                            this.Write = q;
                            return this.Inflate_flush(z, r);
                        }

                        this.mode = DONE;
                        goto case DONE;

                    case DONE:
                        r = CompressionState.ZSTREAMEND;

                        this.Bitb = b;
                        this.Bitk = k;
                        z.AvailableIn = n;
                        z.TotalIn += p - z.NextInIndex;
                        z.NextInIndex = p;
                        this.Write = q;
                        return this.Inflate_flush(z, r);

                    case BAD:
                        r = CompressionState.ZDATAERROR;

                        this.Bitb = b;
                        this.Bitk = k;
                        z.AvailableIn = n;
                        z.TotalIn += p - z.NextInIndex;
                        z.NextInIndex = p;
                        this.Write = q;
                        return this.Inflate_flush(z, r);

                    default:
                        r = CompressionState.ZSTREAMERROR;

                        this.Bitb = b;
                        this.Bitk = k;
                        z.AvailableIn = n;
                        z.TotalIn += p - z.NextInIndex;
                        z.NextInIndex = p;
                        this.Write = q;
                        return this.Inflate_flush(z, r);
                }
            }
        }

        internal void Free(ZStream z)
        {
            this.Reset(z, null);
            this.Window = null;
            this.hufts = null;

            // ZFREE(z, s);
        }

        internal void Set_dictionary(byte[] d, int start, int n)
        {
            Buffer.BlockCopy(d, start, this.Window, 0, n);
            this.Read = this.Write = n;
        }

        // Returns true if inflate is currently at the end of a block generated
        // by Z_SYNC_FLUSH or Z_FULL_FLUSH.
        internal CompressionState Sync_point() => this.mode == LENS ? CompressionState.ZSTREAMEND : CompressionState.ZOK;

        // copy as much as possible from the sliding window to the output area
        internal CompressionState Inflate_flush(ZStream zStream, CompressionState state)
        {
            int n;
            int p;
            int q;

            // local copies of source and destination pointers
            p = zStream.NextOutIndex;
            q = this.Read;

            // compute number of bytes to copy as far as end of window
            n = (q <= this.Write ? this.Write : this.End) - q;
            if (n > zStream.AvailableOut)
            {
                n = zStream.AvailableOut;
            }

            if (n != 0 && state == CompressionState.ZBUFERROR)
            {
                state = CompressionState.ZOK;
            }

            // update counters
            zStream.AvailableOut -= n;
            zStream.TotalOut += n;

            // update check information
            if (this.doCheck)
            {
                zStream.Adler = this.check = Adler32.Calculate(this.check, this.Window.AsSpan(q, n));
            }

            // copy as far as end of window
            Buffer.BlockCopy(this.Window, q, zStream.NextOut, p, n);
            p += n;
            q += n;

            // see if more to copy at beginning of window
            if (q == this.End)
            {
                // wrap pointers
                q = 0;
                if (this.Write == this.End)
                {
                    this.Write = 0;
                }

                // compute bytes to copy
                n = this.Write - q;
                if (n > zStream.AvailableOut)
                {
                    n = zStream.AvailableOut;
                }

                if (n != 0 && state == CompressionState.ZBUFERROR)
                {
                    state = CompressionState.ZOK;
                }

                // update counters
                zStream.AvailableOut -= n;
                zStream.TotalOut += n;

                // update check information
                if (this.doCheck)
                {
                    zStream.Adler = this.check = Adler32.Calculate(this.check, this.Window.AsSpan(q, n));
                }

                // copy
                Buffer.BlockCopy(this.Window, q, zStream.NextOut, p, n);
                p += n;
                q += n;
            }

            // update pointers
            zStream.NextOutIndex = p;
            this.Read = q;

            // done
            return state;
        }
    }
}
