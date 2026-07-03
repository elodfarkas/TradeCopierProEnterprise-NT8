# TradeCopier Pro Enterprise

**TradeCopier Pro Enterprise** is a NinjaTrader 8 Add-On for execution-based multi-account trade copying.  
It is designed to copy filled master account executions to one or more configured follower accounts, with a safety-first workflow, TEST RUN / LIVE operation modes, preflight validation, routing controls, audit logging, and a professional WPF control center.

> ⚠️ **Risk Disclaimer**  
> This software can submit real orders when LIVE MODE is enabled. Trading futures and financial instruments involves substantial risk. Use TEST RUN mode first, validate all routing, and understand every follower account configuration before enabling live copy routing. This project is provided for educational and operational tooling purposes only and does not constitute financial advice.

---

## Key Features

### TEST RUN / LIVE MODE

TradeCopier Pro Enterprise supports two operation modes:

- **TEST RUN**  
  Simulates follower submissions only. No real follower orders are sent. The system logs what it *would* submit using `DRY_RUN_SUBMIT`.

- **LIVE MODE**  
  Sends real follower orders after successful safety checks and confirmation. LIVE submissions are logged as `LIVE_SUBMIT_ATTEMPT` followed by `SUBMIT`.

The internal settings map TEST RUN to `DryRunMode = true` and LIVE MODE to `DryRunMode = false`.

---

### Single Main Control: ENABLE / DISABLE COPY

The dashboard is simplified around one large main control:

- **ENABLE COPY**  
  Runs the safety workflow, including preflight checks and arming, before enabling routing.

- **DISABLE COPY**  
  Stops new copied orders by moving the engine back to OFF.

The button changes state based on the runtime state:

- `Off`, `Ready`, `Error` → green **ENABLE COPY**
- `On` → red **DISABLE COPY**
- `Arming` → **ARMING...**
- `KillSwitch` → **KILL SWITCH ACTIVE**

This simplified dashboard wraps the internal `StartSafeCopy()` workflow while keeping advanced controls available separately.

---

### Safety-First Workflow

Before copy routing can become active, the system validates configuration through a preflight process.

The preflight validation checks:

- master account availability
- master instrument validity
- at least one enabled follower
- follower account availability
- follower instrument validity
- duplicate follower routes
- invalid multiplier / conversion settings
- follower flat requirements
- runtime-disabled follower states

The `RunPreflight()` process resolves accounts and instruments, validates the configuration, updates the preflight report, computes a configuration hash, and rebuilds account subscriptions when successful.

---

### Execution-Based Copying

The copier listens for master account execution events and routes filled master executions to enabled follower accounts.

For each eligible master execution, the engine:

1. validates runtime state and safety gates,
2. prevents duplicate execution processing,
3. creates a copy batch,
4. calculates follower quantity,
5. runs risk checks,
6. runs slippage checks,
7. either logs a TEST RUN submission or submits a LIVE follower order.

The actual order submission is handled through NinjaTrader account order creation and `Submit()` calls in LIVE MODE.

---

### Follower Configuration

Each follower can be configured with:

- enabled / disabled status
- follower account
- follower instrument
- quantity multiplier
- contract conversion factor
- rounding mode
- min quantity
- max order quantity
- max position size
- daily contract limit
- trades-per-day limit
- allowed order types
- flat-before-enable rules
- out-of-sync behavior
- slippage tick limit

Follower rows are shown in the Control Center grid with runtime information such as expected position, actual position, sync status, last order state, and last error.

---

### Quantity Calculation

Follower order quantity is calculated from:

```text
master quantity × quantity multiplier × contract conversion factor
```

Supported rounding modes include:

- Floor
- Ceiling
- RoundNearest
- MinimumOneIfMasterNonZero
- SkipIfBelowOne

This allows flexible routing such as ES → MES, NQ → MNQ, or proportional account sizing.

---

### Risk Controls

The built-in risk manager can block follower orders based on:

- invalid quantity
- global max order quantity
- follower max order quantity
- global max daily contracts
- follower max daily contracts
- global max trades per day
- follower max trades per day
- follower max position size
- follower not flat
- follower out-of-sync state

Risk-blocked orders are logged and do not reach the live order router.

---

### Slippage Controls

The slippage system supports multiple modes:

- `Off`
- `WarnOnly`
- `BlockOrder`
- `ConvertToLimit`

The engine can block or convert orders based on max slippage ticks, instrument tick size, missing quote rules, and quote age settings.

---

### Kill Switch

The system includes a Kill Switch state that blocks new copy submissions.

Optional Kill Switch behavior includes:

- cancel working orders
- flatten follower accounts
- include disabled followers
- allow reset only if all followers are flat

The state machine treats `KillSwitch` as a protected emergency state that requires explicit reset logic.

---

### Route Summary Header

The Control Center header displays a compact routing summary:

```text
Master: [MasterAccount] | [MasterInstrument]
[MasterAccount] [MasterInstrument] → [N] followers
Followers: [N] enabled / [M] instruments
```

Follower route details are also available through tooltip-style route summaries, including follower account, instrument, and multiplier.

---

### LIVE MODE Visual Warning

When LIVE MODE is active and the copy engine is ON, the LIVE MODE badge blinks between critical/danger colors to make the live-routing state clearly visible. TEST RUN mode uses a fixed warning color.

---

### Audit Logging

TradeCopier Pro Enterprise maintains an event log inside the UI and can write CSV audit logs.

Logged event types include:

- diagnostics
- state changes
- preflight results
- subscription rebuilds
- TEST RUN submissions
- LIVE submit attempts
- submitted orders
- submit exceptions
- risk blocks
- slippage blocks
- follower errors
- flatten actions
- kill switch reset blocks

CSV audit output is configured through `EnableCsvAudit` and `AuditFileName`.

---

## Control Center Overview

The WPF Control Center includes:

- Dashboard
- Quick Start
- Master Setup
- Followers
- Risk & Safety
- Slippage
- Preflight Check
- Event Log
- Settings / About

The UI uses a sidebar navigation layout, a professional header, mode banners, status indicators, and a simplified main ENABLE / DISABLE COPY workflow.

---

## Recommended Workflow

### 1. Configure Master

Select the account and instrument that will act as the trade source.

Example:

```text
Master: Sim101 | ES 09-26
```

---

### 2. Add Followers

Add one or more follower account routes.

Example:

```text
SimAccount10 → MES 09-26
SimAccount11 → ES 09-26
```

---

### 3. Start in TEST RUN

Use TEST RUN mode first.

In TEST RUN mode:

- no real follower orders are submitted,
- the event log shows what would have been submitted,
- logs use `DRY_RUN_SUBMIT`.

---

### 4. Press ENABLE COPY

The main ENABLE COPY button runs the guided workflow.

It performs:

```text
Preflight → Arming → Ready → Enable Copy
```

If preflight fails, the copier does not enable routing.

---

### 5. Validate Routing

Review:

- Event Log
- follower grid
- master/follower instruments
- expected vs actual positions
- quantity multipliers
- route summary

---

### 6. Switch to LIVE MODE

Only switch to LIVE after successful TEST RUN validation.

LIVE MODE requires confirmation because real follower orders may be submitted.

---

### 7. Monitor Live Routing

When LIVE MODE and ON state are active:

- LIVE MODE badge blinks,
- follower orders are submitted,
- each submit attempt is logged,
- actual submissions are shown as `SUBMIT`.

---

## Installation

Copy the Add-On file into the NinjaTrader 8 custom AddOns folder:

```text
Documents\NinjaTrader 8\bin\Custom\AddOns\
```

The source comment currently references the install path as:

```text
Documents\NinjaTrader 8\bin\Custom\AddOns\TradeCopierProEnterpriseV3.cs
```

The visible product name in the UI is **TradeCopier Pro Enterprise**, while some internal class and file names still retain `V3` naming for compatibility.

After copying the file:

1. Open NinjaTrader 8.
2. Open NinjaScript Editor.
3. Compile the Add-On.
4. Restart NinjaTrader if required.
5. Open from:

```text
Tools → TradeCopier Pro Enterprise
```

---

## Configuration Files

Settings are stored under the NinjaTrader user data directory in:

```text
TradeCopierProEnterpriseV3
```

The settings repository stores configuration in XML and creates backups before replacing the active settings file. CSV audit logs are also written in the same product folder when enabled.

---

## Runtime States

The engine uses a state machine with the following states:

| State | Meaning |
|---|---|
| `Off` | Copying is disabled |
| `Arming` | Safety checks / preflight workflow in progress |
| `Ready` | Preflight passed and copier is ready to enable |
| `On` | Copy routing is active |
| `Paused` | Paused state, available internally / advanced workflow |
| `Error` | Action required |
| `KillSwitch` | Emergency lock active |
| `Disconnected` | Connection uncertainty / recovery state |

The main dashboard simplifies this into a user-facing ENABLE / DISABLE workflow while advanced controls remain available.

---

## Event Examples

### TEST RUN

```text
DRY_RUN_SUBMIT
TEST RUN ONLY - would submit Buy 1 Market MES 09-26. No real order was sent.
```

### LIVE MODE

```text
LIVE_SUBMIT_ATTEMPT
LIVE MODE - submitting Buy 1 Market MES 09-26

SUBMIT
Buy 1 Market
```

---

## Safety Notes

Before using LIVE MODE:

- verify master account,
- verify follower accounts,
- verify instruments,
- verify contract conversions,
- verify quantity multipliers,
- run TEST RUN first,
- confirm routed order sizes,
- confirm follower account permissions,
- use SIM accounts first,
- use small size when validating,
- monitor NinjaTrader Positions and Orders tabs.

Do not rely only on UI status. Always verify actual NinjaTrader account/order state.

---

## Technical Notes

TradeCopier Pro Enterprise is implemented as a NinjaTrader 8 Add-On using C# and WPF.

Core components include:

- AddOn bootstrap
- Control Center window
- ViewModel with dispatcher-safe updates
- settings repository
- state machine
- account subscription manager
- execution router
- quantity calculator
- risk manager
- slippage engine
- position synchronizer
- latency monitor
- audit logger

Observable UI collections and bound properties are updated through dispatcher-safe methods to avoid WPF cross-thread collection issues.

---

## About

**TradeCopier Pro Enterprise**  
Created by **Juhász Előd Farkas**

- GitHub: https://github.com/elodfarkas
- LinkedIn: https://www.linkedin.com/in/elod/

---

## Disclaimer

This software is provided as-is, without warranty of any kind.  
The author is not responsible for trading losses, configuration mistakes, platform issues, broker-side behavior, rejected orders, duplicate routing, latency, slippage, or any other financial consequence.

Use at your own risk. Always test thoroughly in simulation before live usage.
