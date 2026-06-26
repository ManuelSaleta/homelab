NAMESPACE=monitoring

.PHONY: install-loki install-alloy install-promstack grafana-pass all clean

# The 'all' recipe now staggers the deployment
all: install-promstack
	@echo "--- Waiting for Prometheus stack to be ready ---"
	@kubectl rollout status deployment/promstack-kube-prometheus-operator -n $(NAMESPACE) --timeout=300s
	@$(MAKE) install-loki
	@echo "--- Waiting for Loki to be ready ---"
	@kubectl rollout status statefulset/my-loki -n $(NAMESPACE) --timeout=300s
	@$(MAKE) install-alloy

install-loki:
	helm upgrade --install my-loki grafana/loki -n $(NAMESPACE) --create-namespace -f loki-values.yaml

install-alloy:
	helm upgrade --install my-alloy grafana/alloy -n $(NAMESPACE) -f alloy-values.yaml

install-promstack:
	helm upgrade --install promstack prometheus-community/kube-prometheus-stack -n $(NAMESPACE) --create-namespace -f prometheus-values.yaml

clean:
	helm uninstall my-loki my-alloy promstack -n $(NAMESPACE)

grafana-pass:
	@echo "Grafana Admin Password: $(shell kubectl get secret --namespace $(NAMESPACE) promstack-grafana -o jsonpath="{.data.admin-password}" | base64 --decode ; echo)"