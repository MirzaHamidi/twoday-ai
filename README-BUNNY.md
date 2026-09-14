# TwoDay AI - Bunny Magic Containers

Bu proje uzun sure calisan bir Telegram polling worker'idir. HTTP endpoint gerekmez.

## Normal image

```powershell
docker build -t ghcr.io/KULLANICI/twoday-ai:latest .
docker push ghcr.io/KULLANICI/twoday-ai:latest
```

## Mevcut SQLite hafizasini ilk kez tasima

Once botun bilgisayardaki calisan kopyasini durdurun. `twoday-ai.db-wal` ve
`twoday-ai.db-shm` dosyalari varsa temiz bir checkpoint/backup alin. Ardindan
yalnizca ilk dagitim icin migration image olusturun:

```powershell
docker build -f Dockerfile.migrate -t ghcr.io/KULLANICI/twoday-ai:migrate-1 .
docker push ghcr.io/KULLANICI/twoday-ai:migrate-1
```

Migration image, `/data/twoday-ai.db` yoksa paketlenmis veritabanini oraya bir
kez kopyalar. Var olan volume veritabaninin uzerine yazmaz. Ilk acilis ve mesaj
testinden sonra normal `latest` image'ina gecin.

## Bunny ayarlari

- Deployment: Single Region
- Region: Istanbul veya yakin tek bir bolge
- Replicas: minimum 1, maximum 1
- Endpoint: yok
- Persistent volume mount path: `/data`
- Environment variables:
  - `TELEGRAM_BOT_TOKEN`
  - `OPENAI_API_KEY`
  - `DATA_DIR=/data`
- Startup command / arguments: bos birakin

Telegram long polling ve tek SQLite dosyasi nedeniyle uygulamayi birden fazla
replica veya bolgede calistirmayin.
