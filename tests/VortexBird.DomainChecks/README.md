# Verificaciones de dominio y validación

Ejecutar desde `vortexbird-backend`:

```powershell
dotnet run --project tests/VortexBird.DomainChecks
```

El ejecutable termina con código 0 cuando pasan todas las verificaciones y 1 si falla alguna. No usa paquetes externos: permite verificar invariantes incluso cuando el entorno no tiene acceso a NuGet. Cubre reservas, sobreventa, liberación, consumo de inventario, transiciones de la saga, compensación, estados terminales y validación de solicitudes. No reemplaza las pruebas de PostgreSQL, concurrencia, API ni rendimiento.

La validación usa `CreateOrderRequestValidator` en Application, con errores inmutables por campo. La dependencia FluentValidation solicitada se difiere porque el entorno de ejecución no puede restaurar paquetes no disponibles localmente. La interfaz del validador permite sustituir el mecanismo sin cambiar las reglas ni debilitar las validaciones. No se declara FluentValidation como implementado.
