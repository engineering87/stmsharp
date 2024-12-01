# STMSharp

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![issues - stmsharp](https://img.shields.io/github/issues/engineering87/stmsharp)](https://github.com/engineering87/stmsharp/issues)
[![stars - stmsharp](https://img.shields.io/github/stars/engineering87/stmsharp?style=social)](https://github.com/engineering87/stmsharp)

STMSharp is a .NET library for lock-free synchronization using Software Transactional Memory (STM), enabling atomic transactions and efficient multi-threading.

## Features
- **Transaction-based memory model:** Manage and update shared variables without needing locks.
- **Atomic transactions:** Supports atomicity with retry mechanisms and backoff strategies.
- **Conflict detection:** Automatically detects conflicts in the transaction, ensuring data consistency.
- **Exponential backoff:** Includes an automatic backoff strategy for retries, enhancing performance in high-contention scenarios.

## How it works
STMSharp implements the Software Transactional Memory (STM) pattern, allowing you to perform read and write operations on shared variables inside transactions. Each transaction reads the values of shared variables, applies updates, and commits them if no conflicts are detected. If a conflict occurs, the transaction will be retried with an exponential backoff mechanism, which gradually increases the delay between retries.

### Core Components:
1. **Transaction<T>:** The main class representing a transaction. It allows reading from and writing to STM variables.
2. **STMVariable<T>:** A type that encapsulates the shared data and supports STM operations (read/write).
3. **STMEngine:** Provides static methods for managing transactions and conflict resolution.

### How to use it
Here's a basic example of how to use STMSharp in your project:

```csharp
try
{
	// Initialize a shared STM variable
	var sharedVar = new STMVariable<int>(0);

	// Perform an atomic transaction to increment the value
	STMEngine.Atomic(transaction =>
	{
		var value = transaction.Read(sharedVar);
		transaction.Write(sharedVar, value + 1);
	});

	// Perform another atomic transaction
	STMEngine.Atomic(transaction =>
	{
		var value = transaction.Read(sharedVar);
		transaction.Write(sharedVar, value + 1);
	});
}
catch (InvalidOperationException ex)
{
    Console.WriteLine("Transaction failed: " + ex.Message);
}
```

## Contributing
Thank you for considering to help out with the source code!
If you'd like to contribute, please fork, fix, commit and send a pull request for the maintainers to review and merge into the main code base.

 * [Setting up Git](https://docs.github.com/en/get-started/getting-started-with-git/set-up-git)
 * [Fork the repository](https://docs.github.com/en/pull-requests/collaborating-with-pull-requests/working-with-forks/fork-a-repo)
 * [Open an issue](https://github.com/engineering87/stmsharp/issues) if you encounter a bug or have a suggestion for improvements/features

### Licensee
OpenSharpTrace source code is available under MIT License, see license in the source.

### Contact
Please contact at francesco.delre[at]protonmail.com for any details.