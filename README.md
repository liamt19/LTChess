<h1 align="center">
Lizard - A C# chess engine
</h1>

<h2 align="center">
<img src="./Resources/logo.png" width="500">
</h2>

Creating this in my spare time, mainly using it to learn more about optimization and computer games. 
I'm uploading it here so I can keep backups of it and not lose it when my laptop finally dies.

## Ratings
<div align="center">

| Version | Released | [CCRL 40/15](https://www.computerchess.org.uk/ccrl/4040/) | [CCRL Blitz](https://www.computerchess.org.uk/ccrl/404/) | [CCRL FRC](https://www.computerchess.org.uk/ccrl/404FRC/) | Notes |
| ---- | ------------ | ---- | ---- | ---- | --- |
| 10.0 | Jan. 4 2024  | 3368 | 3409 | -    | First non-Stockfish NNUE |
| 10.1 | Jan. 13 2024 | 3430 | -    | -    | Various improvements to search |
| 10.2 | Feb. 9 2024 | 3499 | 3587    | -    | Larger network, more tunes |
| 10.3 | Mar. 8 2024 | 3513 | -    | 3600 | Significant speedups, FRC support |
| 10.4 | Jun. 2 2024 | 3548 | 3635    | 3612 | Larger network, better time management |
| 10.5 | Jul. 13 2024 | 3556 | 3665 | 3685 | Significant speedups, DFRC data |
| 11.0 | Sep. 26 2024 | 3555 | - | 3810 | More heuristics, Selfplay data |
| 11.1 | Nov. 10 2024 | TBD | TBD | TBD | QOL/bugfixes, more DFRC data |

</div>

## Features
### NNUE Evaluation:
Version 11.1 uses a (768x16 -> 2048)x2 -> 8 neural network to evaluate positions, which was trained on incremental self-play data starting with the network from version 10.5. The current size of the dataset is around 4.5 billion positions.

Version 10.5 used a (768x5 -> 1536)x2 -> 8 network, and used ~8 billion positions from a collection of Lc0 datasets.

All networks are trained using [Bullet](https://github.com/jw1912/bullet)

## Building
Using `make` is the easiest way, as this calls `dotnet publish` with the proper parameters.
If your processor supports Avx512, you can also use `make 512` to compile a binary that fully uses those instructions during NNUE inference.

> [!NOTE]
> Requires at least .NET 8.0 or higher to build.

> [!IMPORTANT]
> NNUE networks are served in [a separate repository](https://github.com/liamt19/lizard-nets/) to keep the size of this main repository small. The makefile will automatically take care of this by retrieving the network specified in the [network.txt file in the repo root](/network.txt), but note that compiling directly via `dotnet publish`/`build` will fail unless you download the [latest network file](https://github.com/liamt19/lizard-nets/releases/latest) and manually place it in the directory (or have previously used the makefile).

## Some spotty history:
#### Version 9.3:
Uses its own NNUE evaluation, and began proper parameter testing with [SPRT](https://en.wikipedia.org/wiki/Sequential_probability_ratio_test).
9.3.1 was the last version to be named "LTChess".

#### Version 9.1:
Some major speed improvements to both searches and move generation.
It was rated a bit above 2500 bullet/blitz on Lichess.

#### Version 8.4:
A decent rating increase, and a lot fewer "dumb" moves. 
Many of the commits between 8.0 and 8.4 improved some of the early architectural decisions, and it is now far easier to debug and improve the code. 
It was rated a bit above 2400 bullet/blitz on Lichess.

#### Version 7.0:
A large rating increase (around 250) and was far more polished. 
It was rated a bit above 2000 bullet on Lichess.



## Contributing
If you have any ideas or comments, feel free to create an issue or pull request!
