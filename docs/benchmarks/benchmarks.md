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

| Backoff Type          | AtomicWrite (ns) | AtomicReadOnly (ns) |
|-----------------------|-----------------:|---------------------:|
| **Constant**          |      11,920.00   |         11,066.67    |
| **Exponential**       |      13,222.22   |         11,440.00    |
| **Linear**            |      13,560.00   |         10,710.00    |
| **ExponentialJitter** |      13,611.11   |         13,855.56    |

## Observations

- **Atomic operations** introduce substantially more overhead than direct reads/writes, as expected for a transactional system that performs snapshot capture, validation, and CAS-based commit.
- **Constant backoff** provides the lowest latency for write transactions and competitive read-only performance, making it a solid choice under **low contention**.
- **Exponential and Linear** strategies show slightly higher latency, but remain predictable and stable.
- **ExponentialWithJitter**, while the slowest in this set, offers randomized retry delays that can mitigate synchronized collisions in **high-contention** scenarios.
- Direct `ReadVariable` and `WriteVariable` operations remain extremely fast, confirming the baseline efficiency of non-transactional paths.

## Recommendations

- For **low-contention** or latency-sensitive workloads, use **Constant** or **Linear** backoff.
- For environments where multiple threads may frequently contend on the same STM variables, **Exponential** or **ExponentialWithJitter** can improve fairness and reduce the risk of retry storms.
- For **read-heavy workloads**, prefer `ReadVariable` whenever transactional guarantees are not required; use `AtomicReadOnly` only when isolation is necessary.
- Potential optimization avenues include:
  - reducing per-transaction allocations,
  - refining retry parameters (`MaxAttempts`, minimum and maximum delay),
  - tuning the backoff curve based on domain-specific contention profiles.

## Conclusion

The new measurements show execution times in the range of **10–14 µs** for atomic STM operations.  
These values are typical for software transactional memory implementations in managed languages and **do not indicate regression or structural issues**.  
They reflect the expected cost of isolation, validation, and CAS-based coordination in a realistic benchmark environment targeting .NET 10.