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
dotnet run -- queue watch
```

To reprocess pp of all scores:

```sh
dotnet run -- performance all
```

To pump all scores in `scores` back through the queue for reprocessing:

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

## Environment Variables

### BEATMAP_DOWNLOAD_PATH

URI template to download .osu beatmap files. Used for [realtime processing](#realtime_difficulty).

Defaults to `https://osu.ppy.sh/osu/{0}`.

### DB_HOST

Host for MySQL.

Defaults to `localhost`.

### DB_NAME

Database name.

Defaults to `osu`.

### DB_USER

Database username.

Defaults to `root`.

### DB_PASS

Database password.

### DB_USERS_TABLE

Database users table name; if using the dumps, set this environment variable to "sample_users".

Defaults to `phpbb_users`.

### DD_AGENT_HOST

Host to submit DataDog/StatsD metrics to.

Defaults to `localhost`.

### DD_ENTITY_ID

Enables DataDog origin detection when running in a container. See [DataDog documentation](https://docs.datadoghq.com/developers/dogstatsd/?tab=kubernetes&code-lang=dotnet#origin-detection-over-udp).

### SHARED_INTEROP_SECRET

Secret key used to sign LegacyIO requests to osu-web. Required to award medals.

### PROCESS_USER_MEDALS

Whether to process user medals. Set to `0` to disable processing.

Default is unset (processing enabled).

### PROCESS_USER_TOTALS

Whether to process user total stats. Set to `0` to disable processing.

Default is unset (processing enabled).

### PREFER_REALTIME_DIFFICULTY

Whether to use prefer realtime processing (download beatmaps and compute their difficulty attributes on every processed score), or to rely on database data when possible. Set to `0` to disable processing.

Default is unset (processing enabled).

### REDIS_HOST

Redis connection string; see [here](https://stackexchange.github.io/StackExchange.Redis/Configuration.html#configuration-options) for configuration options.

Defaults to `localhost`

### SCHEMA

Schema version for the queue; see [Schema](https://github.com/ppy/osu-elastic-indexer#schema).

### WRITE_LEGACY_SCORE_PP

Whether to write a legacy score's PP to `osu_scores_high` if applicable to the score. Set to `0` to disable writing.

Default is unset (writing enabled).

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
