# Client login lifecycle regression

This executable references only Client and Contract. Its deterministic protocol fixture is a separate stdio process; it never authenticates with Microsoft, opens a browser, reads Host receipts or accesses the platform credential store.

Run with a fresh output directory:

```sh
dotnet run --project tests/Pcl.ClientLogin.Tests -c Release -- /absolute/fresh-output
```

Checks cover lost admission responses with a stable key, recoverable disconnects, one-time prompt delivery, cancellation before prompt delivery, waiting for the Host terminal state, prompt callback failures, session shutdown/disposal, duplicate login rejection, terminal-state account refresh and package dependency isolation. The fixture checks do not prove real Microsoft authentication or private-pipe reconnection; the integration suite and user-assisted acceptance cover those separately.
