# Performance Testing Summary

## Overview

As part of this project, an updated set of performance benchmarks was executed to evaluate the behavior of **atomic STM operations** under different backoff strategies.  
The benchmarks were performed using **BenchmarkDotNet** on **.NET 10**, with state reset at every iteration to ensure clean and reproducible measurements.

The goal of these tests is to assess how each backoff policy affects the execution time of transactional operations, especially under realistic contention patterns.

## Tested Operations

- **WriteVariable** — direct, non-transactional write  
- **ReadVariable** — direct, non-transactional read  
- **AtomicWrite** — transactional write using STMEngine with retries and backoff  
- **AtomicReadOnly** — transactional read-only operation with snapshot validation

> Note:  
> Direct operations remain extremely fast and allocate no measurable memory.  
> The updated summary focuses on **atomic operations**, where backoff has meaningful effect.

## Backoff Strategies Evaluated

- **Constant**  
- **Exponential**  
- **Linear**  
- **ExponentialWithJitter**

Each strategy influences retry spacing differently and therefore impacts the latency of transactional commits.

## Average Time Summary for Atomic Operations by Backoff Type

The following table shows the **mean execution time (in nanoseconds)** for transactional operations.  
Values represent the average across multiple iterations under controlled conditions.

| Backoff Type               | AtomicWrite (ns) | AtomicReadOnly (ns) |
|----------------------------|-----------------:|--------------------:|
| Linear                     |          5877,78 |             3888,89 |
| Constant                   |          6212,50 |             3966,67 |
| ExponentialWithJitter      |          6377,78 |             4233,33 |
| Exponential                |          8740,00 |             5180,00 |

## Observations

- **Atomic operations** introduce more overhead than direct reads/writes, as expected for a transactional system that performs snapshot capture, validation, and CAS-based commit.
- **Linear backoff** provides the lowest latency overall in this run, with the best results for both `AtomicWrite` and `AtomicReadOnly`.
- **Constant** remains very close to Linear, with slightly higher averages but still highly competitive and stable.
- **ExponentialWithJitter** is marginally slower than Linear/Constant here; its randomized retry spacing can still help reduce synchronized collisions under **high contention**.
- **Exponential** shows the highest latency in this set, which can be expected depending on how quickly the delay grows with retries.
- Direct `ReadVariable` and `WriteVariable` operations remain extremely fast, confirming the baseline efficiency of non-transactional paths.

## Recommendations

- For **low-contention** or latency-sensitive workloads, prefer **Linear** or **Constant** backoff.
- For scenarios where multiple threads frequently contend on the same STM variables, **ExponentialWithJitter** can improve fairness and reduce the risk of retry storms.
- For **read-heavy workloads**, prefer `ReadVariable` whenever transactional guarantees are not required; use `AtomicReadOnly` only when isolation is necessary.
- Potential optimization avenues include:
  - reducing per-transaction allocations,
  - refining retry parameters (`MaxAttempts`, minimum and maximum delay),
  - tuning the backoff curve based on domain-specific contention profiles.

## Conclusion

The new measurements show execution times in the range of **~3.9–8.7 µs** for atomic STM operations (≈ **3,889–8,740 ns**).  
These values are consistent with the expected cost of isolation, validation, and CAS-based coordination in a managed STM implementation targeting .NET 10.