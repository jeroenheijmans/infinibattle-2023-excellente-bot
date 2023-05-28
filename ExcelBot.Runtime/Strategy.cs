﻿using ExcelBot.Runtime.ExcelModels;
using ExcelBot.Runtime.Models;
using ExcelBot.Runtime.Util;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ExcelBot.Runtime
{
    public class Strategy
    {
        private bool hasInitialized = false;
        private readonly Random random;
        private readonly StrategyData strategyData;
        private readonly ISet<Point> possibleFlagCoordinates = new HashSet<Point>();
        private readonly ISet<Point> unmovedOwnPieceCoordinates = new HashSet<Point>();
        private readonly ISet<Point> unrevealedOwnPieceCoordinates = new HashSet<Point>();

        public Strategy(Random random, StrategyData strategyData)
        {
            this.random = random;
            this.strategyData = strategyData;
        }

        public Player MyColor { get; set; }
        public Player OpponentColor { get; set; }

        public BoardSetup initialize(GameInit data)
        {
            MyColor = data.You;
            OpponentColor = data.You == Player.Red ? Player.Blue : Player.Red;

            GetAllHomeCoordinatesFor(OpponentColor).ForEach(c => possibleFlagCoordinates.Add(c));

            if (MyColor == Player.Blue) strategyData.TransposeAll();

            var pieces = random.Next(100) < strategyData.ChanceAtFixedStartingPosition
                ? SetupBoardFromFixedPosition()
                : SetupBoardWithProbabilities();

            pieces.ForEach(p => unmovedOwnPieceCoordinates.Add(p.Position));
            pieces.ForEach(p => unrevealedOwnPieceCoordinates.Add(p.Position));

            hasInitialized = true;

            return new BoardSetup { Pieces = pieces.ToArray() };
        }

        private IEnumerable<Point> GetAllHomeCoordinatesFor(Player player)
        {
            for (int x = 0; x < 10; x++)
            {
                for (int y = 0; y < 4; y++)
                {
                    yield return
                        player == Player.Red
                        ? new Point(x, y)
                        : new Point(x, y).Transpose();
                }
            }
        }

        private IEnumerable<Piece> SetupBoardFromFixedPosition()
        {
            return strategyData.FixedStartGrids
                .OrderBy(_ => Guid.NewGuid()) // quick and dirty shuffle
                .First()
                .StartingPositions
                .Select(tuple => new Piece
                {
                    Rank = tuple.Item1,
                    Position = tuple.Item2
                })
                .ToList();
        }

        private IEnumerable<Piece> SetupBoardWithProbabilities()
        {
            var pieces = new List<Piece>();

            foreach (var grid in strategyData.StartPositionGrids)
            {
                var maxIterations = 10000;
                for (int i = 0; i < maxIterations; i++)
                {
                    var piece = grid.PickStartingPosition(random.Next());
                    if (pieces.Any(x => x.Position == piece.Position)) continue;
                    pieces.Add(piece);
                    break;
                }
            }

            return pieces;
        }

        public Move? Process(GameState state)
        {
            if (!hasInitialized)
            {
                throw new InvalidOperationException("Processing move before initialization is not possible");
            }

            if (state.ActivePlayer == MyColor)
            {
                return DecideNextMove(state);
            }
            else
            {
                ProcessOpponentMove(state);
                return null;
            }
        }

        private Move DecideNextMove(GameState state)
        {
            var move = state.Board
                .Where(c => c.Owner == MyColor) // only my pieces can be moved
                .SelectMany(c => GetPossibleMovesFor(c, state)) // all options from all starting points
                .OrderByDescending(move => move.Score)
                .First();

            unmovedOwnPieceCoordinates.Remove(move.From);

            if (unrevealedOwnPieceCoordinates.Contains(move.From))
            {
                unrevealedOwnPieceCoordinates.Remove(move.From);
                if (!state.Board.First(c => c.Coordinate == move.To).IsOpponentPiece(MyColor))
                {
                    unrevealedOwnPieceCoordinates.Add(move.To);
                }
            }

            return move;
        }

        private IEnumerable<MoveWithDetails> GetPossibleMovesFor(Cell origin, GameState state)
        {
            if (origin.Rank == "Flag" || origin.Rank == "Bomb") return Enumerable.Empty<MoveWithDetails>();

            var deltas = new Point[] { new Point(-1, 0), new Point(+1, 0), new Point(0, -1), new Point(0, +1) };

            return deltas.SelectMany(delta =>
            {
                var result = new List<MoveWithDetails>();
                var steps = 0;
                var target = origin.Coordinate;
                while (steps++ < 1 || origin.Rank == "Scout")
                {
                    target = target + delta;
                    var targetCell = state.Board.FirstOrDefault(c => c.Coordinate == target);
                    if (targetCell == null) break; // Out of bounds
                    if (targetCell.IsWater) break; // Water ends our options
                    if (targetCell.Owner == MyColor) break; // Own pieces block the path

                    var move = new MoveWithDetails
                    {
                        From = origin.Coordinate,
                        To = target,
                        Rank = origin.Rank,
                        WillBeDecisiveVictory = targetCell.IsKnownPiece && targetCell.CanBeDefeatedBy(origin.Rank),
                        WillBeDecisiveLoss = targetCell.IsKnownPiece && targetCell.WillCauseDefeatFor(origin.Rank),
                        WillBeUnknownBattle = targetCell.IsUnknownPiece,
                        IsBattleOnOwnHalf = targetCell.IsPiece && targetCell.IsOnOwnHalf(MyColor),
                        IsBattleOnOpponentHalf = targetCell.IsPiece && targetCell.IsOnOpponentHalf(MyColor),
                        IsMoveTowardsOpponentHalf = IsMoveTowardsOpponentHalf(origin.Coordinate, target),
                        IsMoveWithinOpponentHalf = IsMoveWithinOpponentHalf(origin.Coordinate, target),
                        IsMovingForFirstTime = unmovedOwnPieceCoordinates.Contains(origin.Coordinate),
                        IsMoveForUnrevealedPiece = unrevealedOwnPieceCoordinates.Contains(origin.Coordinate),
                        NetChangeInManhattanDistanceToPotentialFlag =
                            GetSmallestManhattanDistanceToPotentialFlag(state, target)
                            - GetSmallestManhattanDistanceToPotentialFlag(state, origin.Coordinate),
                        Steps = steps,
                    };

                    SetScoreForMove(move);

                    result.Add(move);

                    if (targetCell.Owner != null) break; // Can't jump over pieces, so this stops the line
                }
                return result;
            });
        }

        private double GetSmallestManhattanDistanceToPotentialFlag(GameState state, Point source)
        {
            bool isWaterColumn(Point p) => p.X == 2 || p.X == 3 || p.X == 6 || p.X == 7;

            return state.Board
                .Where(cell => possibleFlagCoordinates.Contains(cell.Coordinate))
                .Select(cell =>
                {
                    double dist = source.DistanceTo(cell.Coordinate);

                    // We'll dislike moving into "water" columns on our own half
                    // because that might block us on a path to the other's flag.
                    // As a shortcut/hack we fake an increase in Manhattan Distance
                    // to avoid scoring this move well.
                    if (Cell.IsOnOwnHalf(MyColor, cell.Coordinate) && isWaterColumn(cell.Coordinate))
                    {
                        dist += 3;
                    }

                    // The Excel sheet has probabilities for where the flag may be,
                    // and we translate this into a virtual decrease in "distance"
                    // by turning high probabilities into lower return values.
                    if (strategyData.OpponentFlagProbabilities.ContainsKey(cell.Coordinate))
                    {
                        int probability = strategyData.OpponentFlagProbabilities[cell.Coordinate];
                        double divider = (100 + probability) / 100;
                        dist /= divider;
                    }

                    return dist;
                })
                .Min();
        }

        private bool IsMoveTowardsOpponentHalf(Point from, Point to) =>
            MyColor == Player.Red
                ? from.Y < 6 && to.Y > from.Y
                : from.Y > 3 && to.Y < from.Y;

        private bool IsMoveWithinOpponentHalf(Point from, Point to) =>
            MyColor == Player.Red
                ? from.Y > 5 && to.Y > 5
                : from.Y < 4 && to.Y < 4;

        private void SetScoreForMove(MoveWithDetails move)
        {
            if (move.WillBeDecisiveVictory) move.Score += strategyData.DecisiveVictoryPoints;
            if (move.WillBeDecisiveLoss) move.Score += strategyData.DecisiveLossPoints;
            if (move.WillBeUnknownBattle && move.IsBattleOnOwnHalf) move.Score += strategyData.UnknownBattleOwnHalfPoints;
            if (move.WillBeUnknownBattle && move.IsBattleOnOpponentHalf) move.Score += strategyData.UnknownBattleOpponentHalfPoints;
            if (move.IsMoveTowardsOpponentHalf) move.Score += strategyData.BonusPointsForMoveTowardsOpponent;
            if (move.IsMoveWithinOpponentHalf) move.Score += strategyData.BonusPointsForMoveWithinOpponentArea;
            if (move.IsMovingForFirstTime) move.Score += strategyData.BonusPointsForMovingPieceForTheFirstTime;
            if (move.IsMoveForUnrevealedPiece) move.Score += strategyData.BonusPointsForMovingUnrevealedPiece;
            
            if (move.NetChangeInManhattanDistanceToPotentialFlag < 0)
                move.Score += strategyData.ScoutJumpsToPotentialFlagsMultiplication
                    ? strategyData.BonusPointsForMovesGettingCloserToPotentialFlags 
                        * (move.Steps > 1 ? Math.Abs(move.NetChangeInManhattanDistanceToPotentialFlag) : 1)
                    : strategyData.BonusPointsForMovesGettingCloserToPotentialFlags;

            var boost = 0;
            if (move.Rank == "Spy") boost = strategyData.BoostForSpy;
            if (move.Rank == "Scout") boost = strategyData.BoostForScout;
            if (move.Rank == "Miner") boost = strategyData.BoostForMiner;
            if (move.Rank == "General") boost = strategyData.BoostForGeneral;
            if (move.Rank == "Marshal") boost = strategyData.BoostForMarshal;

            double boostMultiplier = (move.Score < 0 ? -boost : +boost) + 100;
            move.Score *= boostMultiplier / 100;

            double fuzzynessMultiplier = random.Next(strategyData.FuzzynessFactor) + 100;
            move.Score *= fuzzynessMultiplier / 100;
        }

        private void ProcessOpponentMove(GameState state)
        {
            state.Board
                .Where(c => !c.IsPiece || !c.IsUnknownPiece || !c.IsOnOpponentHalf(MyColor))
                .ForEach(c => possibleFlagCoordinates.Remove(c.Coordinate));

            if (state.LastMove != null)
            {
                possibleFlagCoordinates.Remove(state.LastMove.To);
                possibleFlagCoordinates.Remove(state.LastMove.From);
                unrevealedOwnPieceCoordinates.Remove(state.LastMove.To);
            }
        }
    }
}
