# Little Launcher Store pricing comparison, September 23, 2026

Partner Center Submission 41 (`1152921505701888454`, published) uses `PriceId: Base`.
Submission 42 (`1152921505701960975`, committed with `PriceId: Tier1012`) is in
pre-processing with `targetPublishMode: Manual`. Its x64 and ARM64 packages are
version 1.40.2.0. The Store has not published this submission.

The Partner Center **Review price per market** view showed 240 markets in each
submission. Comparing every displayed market row found 36 changed retail prices.
The base currency and U.S. retail price remain USD 0.99; Canada remains 1.29.
The values below are the market-local amounts displayed by Partner Center, without
currency symbols. They were read after the Tier1012 API commit entered
pre-processing. Microsoft may adjust converted prices later, so check the final
values before publication.

| Market | Published Base | Held Tier1012 |
|---|---:|---:|
| Andorra (AD) | 0,89 | 0,99 |
| Argentina (AR) | 1.330,00 | 15,00 |
| Australia (AU) | 1.50 | 1.45 |
| Austria (AT) | 1,09 | 0,99 |
| Bangladesh (BD) | 119.00 | 79.00 |
| Belgium (BE) | 1,09 | 0,99 |
| Brazil (BR) | 4,95 | 3,45 |
| Bulgaria (BG) | 0,97 | 1,02 |
| Chile (CL) | 959 | 699 |
| Colombia (CO) | 4.000 | 2.900 |
| Egypt (EG) | 48.00 | 9.00 |
| Estonia (EE) | 1,09 | 0,99 |
| Finland (FI) | 1,09 | 0,99 |
| France (FR) | 1,09 | 0,99 |
| Greece (GR) | 1,09 | 0,99 |
| India (IN) | 74.00 | 54.00 |
| Ireland (IE) | 1,09 | 0,99 |
| Italy (IT) | 1,09 | 0,99 |
| Kazakhstan (KZ) | 525,00 | 325,00 |
| Latvia (LV) | 1,09 | 0,99 |
| Lithuania (LT) | 1,09 | 0,99 |
| Monaco (MC) | 0,89 | 0,99 |
| Montenegro (ME) | 0,89 | 0,99 |
| Netherlands (NL) | 1,09 | 0,99 |
| New Zealand (NZ) | 1.70 | 1.50 |
| Nigeria (NG) | 1,499.00 | 199.00 |
| Pakistan (PK) | 279.00 | 99.00 |
| Portugal (PT) | 1,09 | 0,99 |
| San Marino (SM) | 0,89 | 0,99 |
| Slovakia (SK) | 1,09 | 0,99 |
| Slovenia (SI) | 1,09 | 0,99 |
| Spain (ES) | 1,09 | 0,99 |
| Sweden (SE) | 9,90 | 10,00 |
| Türkiye (TR) | 40,00 | 2,75 |
| Ukraine (UA) | 35,00 | 25,00 |
| Vatican City (VA) | 0,89 | 0,99 |

Evidence: [staging run](https://github.com/RyanEwen/LittleLauncher/actions/runs/35877319754),
[held commit run](https://github.com/RyanEwen/LittleLauncher/actions/runs/35878083671),
and the [Microsoft maintainer's tier mapping](https://github.com/microsoft/msstore-cli/pull/175#issuecomment-5791491206).
