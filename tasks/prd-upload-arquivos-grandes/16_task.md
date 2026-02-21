---
status: pending
parallelizable: true
blocked_by: ["11.0", "13.0", "14.0", "15.0"]
---

<task_context>
<domain>infra/devops</domain>
<type>implementation</type>
<scope>configuration</scope>
<complexity>medium</complexity>
<dependencies>none</dependencies>
<unblocks>"17.0"</unblocks>
</task_context>

# Tarefa 16.0: Kubernetes YAMLs (F07)

## Visão Geral

Criar os manifests Kubernetes apenas para a **aplicação** (backend e frontend). PostgreSQL, RabbitMQ e MinIO já existem no cluster. Os manifests incluem Deployments, Services, Ingress (Nginx) com configuração para uploads longos, PVC para TUS (ReadWriteMany via Longhorn), Secrets para credenciais e ConfigMaps para configurações.

## Requisitos

- RF07.2: Manifests apenas para backend e frontend (Deployment, PVC, Ingress, Secrets).
- RF07.3: Ingress com `proxy-body-size: 0` e timeouts de 1 hora.
- RF07.4: MinIO funciona com backend 100% stateless.
- RF07.5: TUS funciona com 2+ réplicas compartilhando PVC ReadWriteMany.

## Subtarefas

- [ ] 16.1 Criar pasta `k8s/` na raiz do projeto
- [ ] 16.2 Criar `k8s/namespace.yaml`:
  - Namespace: `upload-poc`
- [ ] 16.3 Criar `k8s/secrets.yaml`:
  - Secret `upload-poc-secrets` com:
    - `jwt-secret`, `db-connection-string`, `minio-access-key`, `minio-secret-key`, `rabbitmq-password`
  - Valores como base64 (placeholder para o cluster real)
- [ ] 16.4 Criar `k8s/configmap.yaml`:
  - ConfigMap `upload-poc-config` com:
    - `MinIO__Endpoint`, `MinIO__BucketName`, `RabbitMQ__Host`, `TusStorage__Path`, configs de timeout
- [ ] 16.5 Criar `k8s/backend-deployment.yaml`:
  - Deployment `upload-poc-backend`:
    - `replicas: 2` (para testar multi-pod)
    - Container: imagem do backend, porta 8080
    - Env vars via Secret + ConfigMap
    - VolumeMount: PVC para TUS storage em `/app/uploads`
    - Liveness/Readiness probe: `GET /health`
    - Resources: requests/limits
- [ ] 16.6 Criar `k8s/backend-service.yaml`:
  - Service `upload-poc-backend`:
    - Type: ClusterIP
    - Port: 80 → target 8080
- [ ] 16.7 Criar `k8s/backend-pvc.yaml`:
  - PVC `upload-poc-tus-storage`:
    - AccessMode: ReadWriteMany
    - StorageClassName: longhorn
    - Storage: 500Gi (para arquivos grandes)
- [ ] 16.8 Criar `k8s/frontend-deployment.yaml`:
  - Deployment `upload-poc-frontend`:
    - `replicas: 1`
    - Container: imagem do frontend (Nginx), porta 80
    - No volumes (stateless)
- [ ] 16.9 Criar `k8s/frontend-service.yaml`:
  - Service `upload-poc-frontend`:
    - Type: ClusterIP
    - Port: 80 → target 80
- [ ] 16.10 Criar `k8s/ingress.yaml`:
  - Ingress `upload-poc-ingress`:
    - Annotations:
      - `nginx.ingress.kubernetes.io/proxy-body-size: "0"` (sem limite)
      - `nginx.ingress.kubernetes.io/proxy-read-timeout: "3600"` (1h)
      - `nginx.ingress.kubernetes.io/proxy-send-timeout: "3600"` (1h)
      - `nginx.ingress.kubernetes.io/proxy-connect-timeout: "60"`
    - Rules:
      - Host: `upload-poc.local` (configurável)
      - Path `/api` → backend service
      - Path `/upload/tus` → backend service
      - Path `/` → frontend service
- [ ] 16.11 Criar `k8s/kustomization.yaml` para facilitar deploy:
  - Lista todos os resources
- [ ] 16.12 Documentar instruções de deploy no README ou em comentários dos YAMLs

## Sequenciamento

- Bloqueado por: 11.0 (Órfãos), 13.0, 14.0, 15.0 (Frontend features — aplicação completa para empacotar)
- Desbloqueia: 17.0 (Testes — validação final)
- Paralelizável: Sim (pode ser feito em paralelo com 17.0)

## Detalhes de Implementação

### Backend Deployment

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: upload-poc-backend
  namespace: upload-poc
spec:
  replicas: 2
  selector:
    matchLabels:
      app: upload-poc-backend
  template:
    metadata:
      labels:
        app: upload-poc-backend
    spec:
      containers:
        - name: backend
          image: upload-poc-backend:latest
          ports:
            - containerPort: 8080
          envFrom:
            - configMapRef:
                name: upload-poc-config
            - secretRef:
                name: upload-poc-secrets
          volumeMounts:
            - name: tus-storage
              mountPath: /app/uploads
          livenessProbe:
            httpGet:
              path: /health
              port: 8080
            initialDelaySeconds: 15
            periodSeconds: 30
          readinessProbe:
            httpGet:
              path: /health
              port: 8080
            initialDelaySeconds: 5
            periodSeconds: 10
          resources:
            requests:
              memory: "256Mi"
              cpu: "250m"
            limits:
              memory: "1Gi"
              cpu: "1000m"
      volumes:
        - name: tus-storage
          persistentVolumeClaim:
            claimName: upload-poc-tus-storage
```

### PVC (Longhorn ReadWriteMany)

```yaml
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: upload-poc-tus-storage
  namespace: upload-poc
spec:
  accessModes:
    - ReadWriteMany
  storageClassName: longhorn
  resources:
    requests:
      storage: 500Gi
```

### Ingress

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: upload-poc-ingress
  namespace: upload-poc
  annotations:
    nginx.ingress.kubernetes.io/proxy-body-size: "0"
    nginx.ingress.kubernetes.io/proxy-read-timeout: "3600"
    nginx.ingress.kubernetes.io/proxy-send-timeout: "3600"
    nginx.ingress.kubernetes.io/proxy-connect-timeout: "60"
spec:
  ingressClassName: nginx
  rules:
    - host: upload-poc.local
      http:
        paths:
          - path: /api
            pathType: Prefix
            backend:
              service:
                name: upload-poc-backend
                port:
                  number: 80
          - path: /upload/tus
            pathType: Prefix
            backend:
              service:
                name: upload-poc-backend
                port:
                  number: 80
          - path: /
            pathType: Prefix
            backend:
              service:
                name: upload-poc-frontend
                port:
                  number: 80
```

## Critérios de Sucesso

- Todos os manifests são YAML válidos (`kubectl apply --dry-run=client -f k8s/`)
- Backend Deployment com 2 réplicas e PVC ReadWriteMany
- Frontend Deployment stateless (sem volumes)
- Ingress com annotations corretas para uploads longos
- Secrets não contêm valores reais (placeholders em base64)
- PVC usa StorageClassName `longhorn` com AccessMode `ReadWriteMany`
- Liveness/Readiness probes configurados no backend
- Kustomization permite `kubectl apply -k k8s/`
- Documentação de deploy incluída
