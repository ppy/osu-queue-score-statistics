# osu-queue-score-statistics-processor [![dev chat](https://discordapp.com/api/guilds/188630481301012481/widget.png?style=shield)](https://discord.gg/ppy)

This project is at heart an [osu-queue-processor](https://github.com/ppy/osu-queue-processor) that handles various book-keeping tasks in response to scores arriving from users. This includes:

- Awarding medals
- Updating the pp values of individual scores
- Updating user statistics (total scores / pp / rank counts / play time / play count / max combo)

It also offers commands to perform common maintenance on all the above, including tasks like:

- Re-running pp calculations on scores (and users) after a change in algorithm
- Re-running processing of individual scores after a change in processing (ie. a new medal or new tracked statistic).
- Migrating scores from legacy tables to the new format

Some things are not yet fully fleshed out:

- Not all medals are handled
- Ranked score processor is not added

# Getting started

To start the queue processing functionality:

```sh
dotnet run -- queue run
```

To reprocess pp of all scores:

```sh
dotnet run -- performance all
```

To pump all scores in `solo_scores` back through the queue for reprocessing:

```sh
dotnet run -- queue pump-all
```

Note that you will want a queue processor running to handle all the scores that are going to appear in the queue.

# Processs versioning

Processing is versioned (see `ScoreProcessed.ProcessedVersion`), so re-queueing scores which have already been processed previously is a safe operation – they will either be skipped or upgraded (revert-apply).

Each processor has an apply and revert command, so in theory, it should be possible to upgrade scores to a newer version of processing. This was made with the assumption we would gradually be adding new pieces of the puzzle in until we had everything online. If everything is in a good state, this may be less useful.

# Current Versions

This is part of a group of projects which are used in live deployments where the deployed version is critical to producing correct results. The `master` branch tracks ongoing developments. If looking to use the correct version for matching live values, please [consult this wiki page](https://github.com/ppy/osu-infrastructure/wiki/Star-Rating-and-Performance-Points) for the latest information.

# Contributing

Contributions can be made via pull requests to this repository. We hope to credit and reward larger contributions via a [bounty system](https://www.bountysource.com/teams/ppy). If you're unsure of what you can help with, check out the [list of open issues](https://github.com/ppy/osu-queue-score-statistics-processor/issues).

Note that while we already have certain standards in place, nothing is set in stone. If you have an issue with the way code is structured; with any libraries we are using; with any processes involved with contributing, *please* bring it up. I welcome all feedback so we can make contributing to this project as pain-free as possible.

# Environment Setup

## BYO

To setup a testing environment, the minimum requirements are having a MySQL instance available at `localhost:3306` and a redis instance available at `localhost:6379`.

Make sure all data is expendable. It ~~may~~ will be nuked without remorse.

## Docker

You can setup an environment and have it map to the correct localhost ports:

```shell
docker-compose up -d
```

You can stop the environment using:
```shell
docker-compose down
```

Alternatively, you can stop and remove all volumes (ie. nuke your test database) using:
```shell
docker-compose down -v
```

# Licence

The osu! client code, framework, and server-side components are licensed under the [MIT licence](https://opensource.org/licenses/MIT). Please see [the licence file](LICENCE) for more information. [tl;dr](https://tldrlegal.com/license/mit-license) you can do whatever you want as long as you include the original copyright and license notice in any copy of the software/source.

Please note that this *does not cover* the usage of the "osu!" or "ppy" branding in any software, resources, advertising or promotion, as this is protected by trademark law.
