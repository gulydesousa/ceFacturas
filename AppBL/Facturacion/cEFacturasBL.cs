using System;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using System.IO;
using BO.Comun;
using BO.Facturacion;
using BL.Sistema;
using BO.Sistema;
using BL.Catastro;
using BO.Catastro;
using BO.Tasks;
using BL.Tasks;
using BL.Cobros;
using BO.Cobros;
using BL.Comun;
using BO.Resources;
using System.Collections.Generic;
using DL.Facturacion;
using System.Transactions;
using System.Globalization;

namespace BL.Facturacion
{
    public static partial class cEFacturasBL
    {

        enum SERES_Versions { V321, V322 }

        private static DateTime? versionSERES_V322()
        {  //Buscamos en los parametros la fecha que marca el inicio del cambio a la version 3.2.2
            cRespuesta respuesta = new cRespuesta();
            
            DateTime? result = null;
           
            DateTime SERES_V322;
            respuesta = cParametroBL.GetDateTime("SERES_V322", out SERES_V322);
            if (respuesta.Resultado == ResultadoProceso.OK)
                result = SERES_V322;

            return result;
        }

        private static cRespuesta explotacionCodigo(out int result)
        {
            cRespuesta respuesta = new cRespuesta();

            respuesta = cParametroBL.GetInteger("EXPLOTACION_CODIGO", out result);

            return respuesta;
        }

        private static string explotacionFormato(int explotacionCodigo, SERES_Versions versionSeres = SERES_Versions.V322)
        {
            string result;

            if (versionSeres == SERES_Versions.V322)
                result = string.Format("{0}00" , explotacionCodigo);
            else
                result = cAplicacion.FixedLengthString(explotacionCodigo.ToString(), 3, '0', false, false); ;

            return result;
        }

        private static bool validarFactura (cFacturaBO factura)
        {
            bool result = factura != null && !string.IsNullOrEmpty(factura.Numero) && factura.SerieCodigo.HasValue;

            return result;
        }
   
        private static string invoiceNumber(cFacturaBO factura, int explotacion, DateTime fechaSERES_V322)
        {
            string result = string.Empty;

            if (!validarFactura(factura)) return result;
            
            //Formato segun la fecha de envio a SERES.
            if (factura.FecEmisionSERES.HasValue && factura.FecEmisionSERES < fechaSERES_V322)
                result = factura.Numero;
            else
                result = string.Format("{0}-{1}-{2}", explotacionFormato(explotacion, SERES_Versions.V322), factura.SerieCodigo, factura.Numero);
    

            return result;
        }

        private static string invoiceSeriesCode(cFacturaBO factura, int explotacion, DateTime fechaSERES_V322)
        {
            string result = string.Empty;

            if (!validarFactura(factura)) return result;

            //Si la factura fue enviada a SERES antes de la configuración, se envía la explotación como se enviaba antes. 
            if (factura.FecEmisionSERES.HasValue && factura.FecEmisionSERES < fechaSERES_V322)
                result = string.Format("{0}-{1}", explotacionFormato(explotacion, SERES_Versions.V321), factura.SerieCodigo);
            else
                result = string.Format("{0}-{1}", explotacionFormato(explotacion, SERES_Versions.V322), factura.SerieCodigo);

            return result;
        }


        public static cRespuesta GenerarFacturasV321(cFacturasSeleccionBO seleccion, out XmlDocument[] facturasXML, string taskUser, ETaskType? taskType, int? taskNumber, out cBindableList<cFacturaBO> facturasAfectadas, out cEFacturasLog taskLog)
        {
            #region RevisionImportesFacE
            taskLog = new cEFacturasLog();
            string rectifLog = string.Empty;
            string impLog = string.Empty;

            facturasAfectadas = new cBindableList<cFacturaBO>();
            #endregion

            cRespuesta respuesta = new cRespuesta();
            facturasXML = new XmlDocument[0];

            cBindableList<cFacturaBO> facturas = cFacturasBL.Obtener(seleccion, out respuesta);
            if (respuesta.Resultado != ResultadoProceso.OK)
            {
                if (respuesta.Resultado == ResultadoProceso.SinRegistros && facturas.Count == 0)
                    respuesta.Ex = new Exception(Resource.infoNoExistenFacturasParaGenerar);
                return respuesta;
            }

            #region  Face rectificativa de no FacE
            rectifLog = ProcesarRectificativasNoEnviadasFacE(ref facturas);
            #endregion

            //Obtenemos la fecha de inicio de la version SERES v3.2.2
            DateTime? fechaSERES_V322= versionSERES_V322();

            //Establecer número de pasos de la tarea
            if (taskNumber.HasValue && taskType.HasValue && !String.IsNullOrEmpty(taskUser))
                cTaskManagerBL.SetTotalSteps(taskUser, taskType.Value, taskNumber.Value, facturas.Count);

            facturasXML = new XmlDocument[facturas.Count];
           
            #region RevisionImportesFacE
            cRespuesta respuestaKO = new cRespuesta();

            taskLog.logAutoAjustes = autoAjusteLineas_FacE(facturas);

            cBindableList<RevisionImportesFaceBO> facturasKO = cFacturasBL.RevisionImportesFacE(facturas, taskUser, out respuestaKO);
           
            #endregion

            for (int i = 0; i < facturas.Count && respuesta.Resultado == ResultadoProceso.OK; i++)
            {             
                //Si estamos ejecutando en modo tarea...
                if (taskNumber.HasValue && taskType.HasValue && !String.IsNullOrEmpty(taskUser))
                {
                    //Comprobar si se desea cancelar
                    if (cTaskManagerBL.CancelRequested(taskUser, taskType.Value, taskNumber.Value, out respuesta) && respuesta.Resultado == ResultadoProceso.OK)
                        return new cRespuesta();
                    //Incrementar el número de pasos
                    cTaskManagerBL.PerformStep(taskUser, taskType.Value, taskNumber.Value);
                }

                XmlDocument facturaXML = new XmlDocument();
                cFacturaBO factura = facturas[i];

                #region RevisionImportesFacE
                bool omitir = omitirFacE(factura, facturasKO, ref impLog);
               
                //La tarea omite la generación de facturas con discrepancias en los importes.
                //Por ser un array, la posición quedará a null y hay que mirarlo en la tarea antes de continuar
                if (omitir) continue;
                #endregion

                if(fechaSERES_V322.HasValue && fechaSERES_V322 <= AcuamaDateTime.Now)
                    respuesta = GenerarFacturaV322(factura, (DateTime)fechaSERES_V322, out facturaXML);
                else
                    respuesta = GenerarFacturaV321(factura, out facturaXML);

                if (respuesta.Resultado == ResultadoProceso.OK)
                {
                    facturasXML[i] = facturaXML;
                    facturasAfectadas.Add(factura);
                }
                respuesta.Resultado = respuesta.Resultado == ResultadoProceso.SinRegistros ? ResultadoProceso.OK : respuesta.Resultado;
            }

            #region RevisionImportesFacE
            taskLog.logNoEmitidas = string.Concat(rectifLog , impLog);

            taskLog.logNoEmitidas += !string.IsNullOrEmpty(taskLog.logNoEmitidas) ? Environment.NewLine + string.Format(Resource.RevisionImportesFacE_Log, facturas.Count, facturasAfectadas.Count, facturasKO.Count): string.Empty;
            #endregion

            return respuesta;
        }

        public static cRespuesta GenerarFacturasV32(cFacturasSeleccionBO seleccion, out XmlDocument[] facturasXML, string taskUser, ETaskType? taskType, int? taskNumber, out int facturasAfectadas)
        {
            cRespuesta respuesta = new cRespuesta();
            facturasXML = new XmlDocument[0];
            facturasAfectadas = 0;
            cBindableList<cFacturaBO> facturas = cFacturasBL.Obtener(seleccion, out respuesta);
            if (respuesta.Resultado != ResultadoProceso.OK)
            {
                if (respuesta.Resultado == ResultadoProceso.SinRegistros && facturas.Count == 0)
                    respuesta.Ex = new Exception(Resource.infoNoExistenFacturasParaGenerar);
                return respuesta;
            }

            //Establecer número de pasos de la tarea
            if (taskNumber.HasValue && taskType.HasValue && !String.IsNullOrEmpty(taskUser))
                cTaskManagerBL.SetTotalSteps(taskUser, taskType.Value, taskNumber.Value, facturas.Count);

            facturasXML = new XmlDocument[facturas.Count];
            for (int i = 0; i < facturas.Count && respuesta.Resultado == ResultadoProceso.OK; i++)
            {
                //Si estamos ejecutando en modo tarea...
                if (taskNumber.HasValue && taskType.HasValue && !String.IsNullOrEmpty(taskUser))
                {
                    //Comprobar si se desea cancelar
                    if (cTaskManagerBL.CancelRequested(taskUser, taskType.Value, taskNumber.Value, out respuesta) && respuesta.Resultado == ResultadoProceso.OK)
                        return new cRespuesta();
                    //Incrementar el número de pasos
                    cTaskManagerBL.PerformStep(taskUser, taskType.Value, taskNumber.Value);
                }

                XmlDocument facturaXML = new XmlDocument();
                cFacturaBO factura = facturas[i];
                respuesta = GenerarFacturaV32(factura, out facturaXML);
                if (respuesta.Resultado == ResultadoProceso.OK)
                {
                    facturasXML[i] = facturaXML;
                    facturasAfectadas = facturasAfectadas + 1;
                }
                respuesta.Resultado = respuesta.Resultado == ResultadoProceso.SinRegistros ? ResultadoProceso.OK : respuesta.Resultado;
            }

            return respuesta;
        }

        private static double Precision<T>(T valor, int precision)
        {
            double number = double.Parse((dynamic)valor.ToString());

            return Math.Round(number, precision, MidpointRounding.AwayFromZero);
        }

        private static double PrecisionV322<T>(T valor)
        {
            return Precision(valor, 2);
        }

        private static double TotalFacturadoV322(FacturaElectronicaV321.InvoiceType[] facturas)
        {
            double result = 0.00;

            result = facturas.Sum(x => TotalFacturaV322(x.InvoiceTotals));
 
            return result;
        }

        private static double TotalFacturaV322(FacturaElectronicaV321.InvoiceTotalsType totales)
        {
            double result = 0.00;

            result = totales == null  ? 0 : PrecisionV322(totales.TotalExecutableAmount);
            
            return result;
        }

        //****************************************************************
        //LOS PROBLEMAS DE RENDIMIENTO DE ESTE MODULO NO SE PUEDEN ACHACAR AL CODIGO DE LA CLASE
        //LA LENTITUD QUE APRECIAMOS ACTUALMENTE EN PRODUCCIÓN VIENE DE SU INTERACCION CON LA BASE DE DATOS (SPs)

        //AUN ASÍ, EL CODIGO DE ESTE METODO ES DEMASIADO EXTENSO Y DEBERIA SER REVISADO

        //LA LEGIBILIDAD Y MANTENIBILIDAD DEL CÓDIGO SON ASPECTOS CLAVE A MEJORAR 
        //****************************************************************
        //En esta demostración...
        //1. COMENZAMOS SOLICITANDO A COPILOT SUGERENCIAS PARA OPTIMIZAR EL CODIGO
        //2. CONTINUAMOS DIVIDIENDO EL METODO EN VARIOS METODOS MAS PEQUEÑOS >>>> fichero InvoideLine.cs >>>>>>
        //3. FINALIZAMOS CREANDO UN WORKFLOW EN "GITHUB ACTIONS"
        //   NOS LANZARÁ ADVERTENCIAS
        //   CUANDO ENCUENTRA PATRONES DE CODIGO QUE DEBERÍAN SER REVISADOS

        //****************************************************************
        //COMPLEJIDAD EXCESIVA DEL METODO casi 800 Lineas!!!!
        public static cRespuesta GenerarFacturaV322(cFacturaBO factura, DateTime fechaSERES_V322, out XmlDocument facturaXML)
        {
            cRespuesta respuesta = new cRespuesta();
            FacturaElectronicaV321.Facturae eFactura = new FacturaElectronicaV321.Facturae();
            facturaXML = new XmlDocument();

            cInmuebleBO inmueble = new cInmuebleBO();
            cIncilecBO incidencia = new cIncilecBO();

            short sociedadCodigo;
            cSociedadBO sociedad = new cSociedadBO();
            respuesta = cParametroBL.GetShort("SOCIEDAD_POR_DEFECTO", out sociedadCodigo);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            string bicElectronico;
            respuesta = cParametroBL.GetString("BIC_ELECTRONICO", out bicElectronico);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;
            string ibanElectronico;
            respuesta = cParametroBL.GetString("IBAN_ELECTRONICO", out ibanElectronico);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            string diasPagoVoluntario;
            respuesta = cParametroBL.GetString("DIAS_PAGO_VOLUNTARIO", out diasPagoVoluntario);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            sociedad.Codigo = sociedadCodigo;
            cSociedadBL.Obtener(ref sociedad, out respuesta);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            cFacturasBL.ObtenerLineas(ref factura, out respuesta);
            if (respuesta.Resultado == ResultadoProceso.OK)
                cFacturasBL.ObtenerImporteFacturado(ref factura, null, precision: 2);
            else
                return respuesta;

            int explotacion;
            string codigoExplotacion = string.Empty;
            
            respuesta = explotacionCodigo(out explotacion);
            if (respuesta.Resultado == ResultadoProceso.OK)
                codigoExplotacion = explotacionFormato(explotacion, SERES_Versions.V322);
            else
                return respuesta;
            
                
            decimal totalFacturadoTodas = factura.TotalFacturado.HasValue ? factura.TotalFacturado.Value : 0;

            eFactura.FileHeader = new FacturaElectronicaV321.FileHeaderType();

            eFactura.FileHeader.Batch = new FacturaElectronicaV321.BatchType();
            eFactura.FileHeader.Batch.InvoicesCount = 1;
            eFactura.FileHeader.Batch.BatchIdentifier = "ES" + sociedad.Nif + factura.Numero.ToString() + "-" + codigoExplotacion + "-" + factura.SerieCodigo.ToString();
            eFactura.FileHeader.Batch.TotalInvoicesAmount = new FacturaElectronicaV321.AmountType();
            eFactura.FileHeader.Batch.TotalInvoicesAmount.TotalAmount = Convert.ToDouble(totalFacturadoTodas.ToString("N2"));
            eFactura.FileHeader.Batch.TotalOutstandingAmount = new FacturaElectronicaV321.AmountType();
            eFactura.FileHeader.Batch.TotalOutstandingAmount.TotalAmount = Convert.ToDouble(totalFacturadoTodas.ToString("N2"));
            eFactura.FileHeader.Batch.TotalExecutableAmount = new FacturaElectronicaV321.AmountType();
            eFactura.FileHeader.Batch.TotalExecutableAmount.TotalAmount = Convert.ToDouble(totalFacturadoTodas.ToString("N2"));
            eFactura.FileHeader.Batch.InvoiceCurrencyCode = new FacturaElectronicaV321.CurrencyCodeType();
            eFactura.FileHeader.Batch.InvoiceCurrencyCode = FacturaElectronicaV321.CurrencyCodeType.EUR;

            eFactura.Parties = new FacturaElectronicaV321.PartiesType();

            eFactura.Parties.SellerParty = new FacturaElectronicaV321.BusinessType();
            eFactura.Parties.SellerParty.TaxIdentification = new FacturaElectronicaV321.TaxIdentificationType();
            eFactura.Parties.SellerParty.TaxIdentification.PersonTypeCode = FacturaElectronicaV321.PersonTypeCodeType.J;
            eFactura.Parties.SellerParty.TaxIdentification.ResidenceTypeCode = FacturaElectronicaV321.ResidenceTypeCodeType.R;
            eFactura.Parties.SellerParty.TaxIdentification.TaxIdentificationNumber = "ES" + sociedad.Nif;

            string cb;
            respuesta = cParametroBL.GetString("CB_SERES", out cb);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            FacturaElectronicaV321.AddressType bAddressType = new FacturaElectronicaV321.AddressType();
            if (!String.IsNullOrEmpty(sociedad.Nacion))
                bAddressType.CountryCode = (FacturaElectronicaV321.CountryType)Enum.Parse(typeof(FacturaElectronicaV321.CountryType), sociedad.Nacion);
            bAddressType.Address = sociedad.Domicilio;
            bAddressType.PostCode = sociedad.CPostal;
            bAddressType.Province = sociedad.Provincia;
            bAddressType.Town = sociedad.Poblacion;

            string nombreExplotacion;
            respuesta = cParametroBL.GetString("EXPLOTACION", out nombreExplotacion);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            eFactura.Parties.SellerParty.AdministrativeCentres = new FacturaElectronicaV321.AdministrativeCentreType[1];
            eFactura.Parties.SellerParty.AdministrativeCentres[0] = new FacturaElectronicaV321.AdministrativeCentreType();
            eFactura.Parties.SellerParty.AdministrativeCentres[0].RoleTypeCodeSpecified = true;
            eFactura.Parties.SellerParty.AdministrativeCentres[0].RoleTypeCode = FacturaElectronicaV321.RoleTypeCodeType.Item09;
            eFactura.Parties.SellerParty.AdministrativeCentres[0].CentreCode = cb;
            eFactura.Parties.SellerParty.AdministrativeCentres[0].Name = nombreExplotacion;
            eFactura.Parties.SellerParty.AdministrativeCentres[0].Item = bAddressType;

            FacturaElectronicaV321.LegalEntityType bEntidadType = new FacturaElectronicaV321.LegalEntityType();
            bEntidadType.CorporateName = sociedad.Nombre;
            bEntidadType.TradeName = sociedad.Nombre;
            bEntidadType.ContactDetails = new FacturaElectronicaV321.ContactDetailsType();
            bEntidadType.ContactDetails.Telephone = sociedad.Telefono1;
            bEntidadType.ContactDetails.ElectronicMail = sociedad.Email;

            bEntidadType.Item = bAddressType;
            eFactura.Parties.SellerParty.Item = bEntidadType;

            eFactura.Invoices = new FacturaElectronicaV321.InvoiceType[1];

            cContratoBO contrato = new cContratoBO();
            contrato.Codigo = factura.ContratoCodigo.Value;
            contrato.Version = factura.ContratoVersion.Value;
            cContratoBL.Obtener(ref contrato, out respuesta);
            factura.Contrato = contrato;

            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            eFactura.Parties.BuyerParty = new FacturaElectronicaV321.BusinessType();

            // Obtenemos el ISO 2 del país del pagador
            cCatalogoPaisBO catalogoPais = cCatalogosPaisesBL.Obtener(String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularNacion : contrato.PagadorNacion, out respuesta);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            eFactura.Parties.BuyerParty.TaxIdentification = new FacturaElectronicaV321.TaxIdentificationType();
            eFactura.Parties.BuyerParty.TaxIdentification.PersonTypeCode = cVarios.ValidateCIF(String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularDocIden : contrato.PagadorDocIden) ? FacturaElectronicaV321.PersonTypeCodeType.J : FacturaElectronicaV321.PersonTypeCodeType.F;
            eFactura.Parties.BuyerParty.TaxIdentification.TaxIdentificationNumber = catalogoPais.ISOAlfa2 + (String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularDocIden : contrato.PagadorDocIden);
            eFactura.Parties.BuyerParty.TaxIdentification.ResidenceTypeCode = FacturaElectronicaV321.ResidenceTypeCodeType.R;

            // Dirección de la persona jurídica/fisica de la factura
            FacturaElectronicaV321.AddressType oAddressType = new FacturaElectronicaV321.AddressType();
            if (!String.IsNullOrEmpty(String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularNacion : contrato.PagadorNacion))
                oAddressType.CountryCode = (FacturaElectronicaV321.CountryType)Enum.Parse(typeof(FacturaElectronicaV321.CountryType), String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularNacion : contrato.PagadorNacion);
            oAddressType.Address = String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularDireccion : contrato.PagadorDireccion;
            oAddressType.PostCode = String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularCpostal : contrato.PagadorCpostal;
            oAddressType.Province = String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularProvincia : contrato.PagadorProvincia;
            oAddressType.Town = String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularPoblacion : contrato.PagadorPoblacion;

            eFactura.Parties.BuyerParty.AdministrativeCentres = new FacturaElectronicaV321.AdministrativeCentreType[4];
            eFactura.Parties.BuyerParty.AdministrativeCentres[0] = new FacturaElectronicaV321.AdministrativeCentreType();
            eFactura.Parties.BuyerParty.AdministrativeCentres[0].CentreCode = contrato.FacturaeOficinaContable;
            eFactura.Parties.BuyerParty.AdministrativeCentres[0].RoleTypeCodeSpecified = true;
            eFactura.Parties.BuyerParty.AdministrativeCentres[0].RoleTypeCode = FacturaElectronicaV321.RoleTypeCodeType.Item01;
            eFactura.Parties.BuyerParty.AdministrativeCentres[0].Item = oAddressType;
            eFactura.Parties.BuyerParty.AdministrativeCentres[0].CentreDescription = "Oficina contable";

            eFactura.Parties.BuyerParty.AdministrativeCentres[1] = new FacturaElectronicaV321.AdministrativeCentreType();
            eFactura.Parties.BuyerParty.AdministrativeCentres[1].CentreCode = contrato.FacturaeOrganismoGestor;
            eFactura.Parties.BuyerParty.AdministrativeCentres[1].RoleTypeCodeSpecified = true;
            eFactura.Parties.BuyerParty.AdministrativeCentres[1].RoleTypeCode = FacturaElectronicaV321.RoleTypeCodeType.Item02;
            eFactura.Parties.BuyerParty.AdministrativeCentres[1].Item = oAddressType;
            eFactura.Parties.BuyerParty.AdministrativeCentres[1].CentreDescription = "Órgano Gestor";

            eFactura.Parties.BuyerParty.AdministrativeCentres[2] = new FacturaElectronicaV321.AdministrativeCentreType();
            eFactura.Parties.BuyerParty.AdministrativeCentres[2].CentreCode = contrato.FacturaeUnidadTramitadora;
            eFactura.Parties.BuyerParty.AdministrativeCentres[2].RoleTypeCodeSpecified = true;
            eFactura.Parties.BuyerParty.AdministrativeCentres[2].RoleTypeCode = FacturaElectronicaV321.RoleTypeCodeType.Item03;
            eFactura.Parties.BuyerParty.AdministrativeCentres[2].Item = oAddressType;
            eFactura.Parties.BuyerParty.AdministrativeCentres[2].CentreDescription = "Unidad Tramitadora";

            eFactura.Parties.BuyerParty.AdministrativeCentres[3] = new FacturaElectronicaV321.AdministrativeCentreType();
            if (!String.IsNullOrEmpty(contrato.FacturaeOrganoProponente))
                eFactura.Parties.BuyerParty.AdministrativeCentres[3].CentreCode = contrato.FacturaeOrganoProponente;
            eFactura.Parties.BuyerParty.AdministrativeCentres[3].RoleTypeCodeSpecified = true;
            eFactura.Parties.BuyerParty.AdministrativeCentres[3].RoleTypeCode = FacturaElectronicaV321.RoleTypeCodeType.Item04;
            eFactura.Parties.BuyerParty.AdministrativeCentres[3].Item = oAddressType;
            eFactura.Parties.BuyerParty.AdministrativeCentres[3].CentreDescription = "Subdirección de compras";

            if (cVarios.ValidateCIF(String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularDocIden : contrato.PagadorDocIden))
            {
                FacturaElectronicaV321.LegalEntityType personaJuridica = new FacturaElectronicaV321.LegalEntityType();

                personaJuridica.CorporateName = String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularNombre : contrato.PagadorNombre;
                personaJuridica.ContactDetails = new FacturaElectronicaV321.ContactDetailsType();
                personaJuridica.ContactDetails.Telephone = contrato.Telefono1;
                personaJuridica.ContactDetails.ElectronicMail = contrato.Email;

                personaJuridica.Item = oAddressType;
                eFactura.Parties.BuyerParty.Item = personaJuridica;
            }
            else
            {
                FacturaElectronicaV321.IndividualType personaFisica = new FacturaElectronicaV321.IndividualType();
                personaFisica.Name = String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularNombre : contrato.PagadorNombre;
                personaFisica.FirstSurname = String.Empty;
                personaFisica.ContactDetails = new FacturaElectronicaV321.ContactDetailsType();
                personaFisica.ContactDetails.Telephone = contrato.Telefono1;
                personaFisica.ContactDetails.ElectronicMail = contrato.Email;

                personaFisica.Item = oAddressType;
                eFactura.Parties.BuyerParty.Item = personaFisica;
            }

            FacturaElectronicaV321.PlaceOfIssueType oPlaceOfIssueType = new FacturaElectronicaV321.PlaceOfIssueType();// Lugar donde se emite la factura
            oPlaceOfIssueType.PlaceOfIssueDescription = sociedad.Poblacion;
            oPlaceOfIssueType.PostCode = sociedad.CPostal;

            cPerzonaBO perzona = new cPerzonaBO();
            if (factura.PeriodoCodigo.Substring(0, 1) != "0")
            {
                perzona.CodigoPeriodo = factura.PeriodoCodigo;
                perzona.CodigoZona = factura.ZonaCodigo;
                new cPerzonaBL().Obtener(ref perzona, out respuesta);
                if (respuesta.Resultado != ResultadoProceso.OK || !perzona.FPeriodoDesde.HasValue || !perzona.FPeriodoHasta.HasValue)
                    return respuesta;
            }

            // Periodo de facturación
            // Periodo de facturación no se muestra para contratos de Soria del inserso
            FacturaElectronicaV321.PeriodDates oPeriodDates = new FacturaElectronicaV321.PeriodDates();
            if ((nombreExplotacion == "Soria") && ((factura.ContratoCodigo.Value == 31691 || factura.ContratoCodigo.Value == 31692 || factura.ContratoCodigo.Value == 192)))
            {
                oPeriodDates.StartDate = factura.FechaLecturaAnterior.Value;
                oPeriodDates.EndDate =   factura.FechaLecturaFactura.Value;
            }
            else
            {

                oPeriodDates.StartDate = factura.PeriodoCodigo.Substring(0, 1) != "0" ? perzona.FPeriodoDesde.Value : new DateTime(factura.Fecha.Value.Year, factura.Fecha.Value.Month, 1);
                oPeriodDates.EndDate = factura.PeriodoCodigo.Substring(0, 1) != "0" ? perzona.FPeriodoHasta.Value : new DateTime(factura.Fecha.Value.Year, factura.Fecha.Value.Month, DateTime.DaysInMonth(factura.Fecha.Value.Year, factura.Fecha.Value.Month));
            }
            //((XmlEnumAttribute)typeof(ReasonCodeType) factura.RazRectcod

            FacturaElectronicaV321.CorrectiveType rectificativa = null;
            if (factura.Version > 1)
            {

                respuesta = cFacturasBL.ObtenerRectificada(ref factura);
                if (respuesta.Resultado != ResultadoProceso.OK)
                    return respuesta;
                FacturaElectronicaV321.ReasonCodeType razonCodigo = new FacturaElectronicaV321.ReasonCodeType();
                FacturaElectronicaV321.ReasonDescriptionType razonDescripcion = new FacturaElectronicaV321.ReasonDescriptionType();

                FacturaElectronicaV321.CorrectionMethodType metodoCorreccion = new FacturaElectronicaV321.CorrectionMethodType();
                FacturaElectronicaV321.CorrectionMethodDescriptionType metodoDescripcion = new FacturaElectronicaV321.CorrectionMethodDescriptionType();

                var razonCodigo = GetEnumValue<FacturaElectronicaV321.ReasonCodeType>(factura.RazRectcod);
                var razonDescripcion = GetEnumValue<FacturaElectronicaV321.ReasonDescriptionType>(factura.RazRectDescType);
                var metodoCorreccion = GetEnumValue<FacturaElectronicaV321.CorrectionMethodType>(factura.MeRect);
                var metodoDescripcion = GetEnumValue<FacturaElectronicaV321.CorrectionMethodDescriptionType>(factura.MeRectType);
                
                foreach (var value in Enum.GetValues(typeof(FacturaElectronicaV321.ReasonCodeType)))
                    if (((XmlEnumAttribute)typeof(FacturaElectronicaV321.ReasonCodeType).GetMember(value.ToString())[0].GetCustomAttributes(typeof(XmlEnumAttribute), false)[0]).Name == factura.RazRectcod)
                    {
                        razonCodigo = (FacturaElectronicaV321.ReasonCodeType)value; break;
                    }
                foreach (var value in Enum.GetValues(typeof(FacturaElectronicaV321.ReasonDescriptionType)))
                    if (((XmlEnumAttribute)typeof(FacturaElectronicaV321.ReasonDescriptionType).GetMember(value.ToString())[0].GetCustomAttributes(typeof(XmlEnumAttribute), false)[0]).Name == factura.RazRectDescType)
                    {
                        razonDescripcion = (FacturaElectronicaV321.ReasonDescriptionType)value; break;
                    }
                foreach (var value in Enum.GetValues(typeof(FacturaElectronicaV321.CorrectionMethodType)))
                    if (((XmlEnumAttribute)typeof(FacturaElectronicaV321.CorrectionMethodType).GetMember(value.ToString())[0].GetCustomAttributes(typeof(XmlEnumAttribute), false)[0]).Name == factura.MeRect)
                    {
                        metodoCorreccion = (FacturaElectronicaV321.CorrectionMethodType)value; break;
                    }
                foreach (var value in Enum.GetValues(typeof(FacturaElectronicaV321.CorrectionMethodDescriptionType)))
                    if (((XmlEnumAttribute)typeof(FacturaElectronicaV321.CorrectionMethodDescriptionType).GetMember(value.ToString())[0].GetCustomAttributes(typeof(XmlEnumAttribute), false)[0]).Name == factura.MeRectType)
                    {
                        metodoDescripcion = (FacturaElectronicaV321.CorrectionMethodDescriptionType)value; break;
                    }
            
                rectificativa = new FacturaElectronicaV321.CorrectiveType()
                {
                    InvoiceNumber     = invoiceNumber(factura.FacturaRectificada, explotacion, fechaSERES_V322),
                    InvoiceSeriesCode = invoiceSeriesCode(factura.FacturaRectificada, explotacion, fechaSERES_V322),

                    ReasonCode = razonCodigo,
                    ReasonDescription = razonDescripcion,

                    TaxPeriod = new FacturaElectronicaV321.PeriodDates()
                    {
                        StartDate = factura.PeriodoCodigo.Substring(0, 1) != "0" ? perzona.FPeriodoDesde.Value : new DateTime(factura.Fecha.Value.Year, factura.Fecha.Value.Month, 1),
                        EndDate = factura.PeriodoCodigo.Substring(0, 1) != "0" ? perzona.FPeriodoHasta.Value : new DateTime(factura.Fecha.Value.Year, factura.Fecha.Value.Month, DateTime.DaysInMonth(factura.Fecha.Value.Year, factura.Fecha.Value.Month)),
                    },

                    CorrectionMethod = metodoCorreccion,
                    CorrectionMethodDescription = metodoDescripcion,
                };
            }

            FacturaElectronicaV321.InvoiceType facturaIndividual =
                new FacturaElectronicaV321.InvoiceType()
                {
                    InvoiceHeader = new FacturaElectronicaV321.InvoiceHeaderType()
                    {
                        InvoiceNumber     = invoiceNumber(factura, explotacion, fechaSERES_V322),
                        InvoiceSeriesCode = invoiceSeriesCode(factura, explotacion, fechaSERES_V322),
                        InvoiceDocumentType = FacturaElectronicaV321.InvoiceDocumentTypeType.FC,
                        InvoiceClass = factura.Version == 1 ? FacturaElectronicaV321.InvoiceClassType.OO : FacturaElectronicaV321.InvoiceClassType.OR, // Original o Rectificativa
                        Corrective = rectificativa,
                    },

                    InvoiceIssueData = new FacturaElectronicaV321.InvoiceIssueDataType()
                    {
                        IssueDate = factura.Fecha.Value,
                        PlaceOfIssue = oPlaceOfIssueType,
                        InvoicingPeriod = oPeriodDates,
                        InvoiceCurrencyCode = FacturaElectronicaV321.CurrencyCodeType.EUR,
                        TaxCurrencyCode = FacturaElectronicaV321.CurrencyCodeType.EUR,
                        LanguageName = FacturaElectronicaV321.LanguageCodeType.es,
                    }
                };

            if (/*contrato.FacturaePortal == cContratoBO.EFacturaePortal.JuntaCastillaMancha &&*/ String.IsNullOrEmpty(contrato.Iban))
            {
                FacturaElectronicaV321.InstallmentType detallesDelPago = new FacturaElectronicaV321.InstallmentType()
                {
                    PaymentMeans = FacturaElectronicaV321.PaymentMeansType.Item04,

                    AccountToBeCredited = new FacturaElectronicaV321.AccountType()
                    {

                        ItemElementName = FacturaElectronicaV321.ItemChoiceType.IBAN,
                        Item = ibanElectronico,
                        BIC = bicElectronico
                    }
                };

                detallesDelPago.InstallmentAmount = totalFacturadoTodas.ToString();
                detallesDelPago.InstallmentDueDate = factura.Fecha.Value.AddDays(Convert.ToDouble(diasPagoVoluntario));

                facturaIndividual.PaymentDetails = new FacturaElectronicaV321.InstallmentType[1];
                facturaIndividual.PaymentDetails[0] = detallesDelPago;
            }

            eFactura.Invoices[0] = facturaIndividual;

            decimal totalFacturado = 0;

            int totalFilasImpuesto = 0;
            bool insertado = false;

            // Impuestos (Agrupados por % de impuesto)
            eFactura.Invoices[0].TaxesOutputs = new FacturaElectronicaV321.TaxOutputType[factura.LineasFactura.Count];
            for (int i = 0; i < factura.LineasFactura.Count && respuesta.Resultado == ResultadoProceso.OK; i++)
            {
                totalFacturado += factura.LineasFactura[i].Total;
                insertado = false;

                for (int imp = 0; imp < totalFilasImpuesto; imp++)
                {
                    if (eFactura.Invoices[0].TaxesOutputs[imp].TaxRate == Convert.ToDouble(factura.LineasFactura[i].PtjImpuesto.ToString("0.00")))
                    {
                        eFactura.Invoices[0].TaxesOutputs[imp].TaxableBase.TotalAmount = Convert.ToDouble((Convert.ToDouble(eFactura.Invoices[0].TaxesOutputs[imp].TaxableBase.TotalAmount.ToString("N2")) + Convert.ToDouble(factura.LineasFactura[i].CBase.ToString("N2"))).ToString("N2"));
                        //Los impuestos se totalizan por tasa con maxima precision (8 decimales)
                        eFactura.Invoices[0].TaxesOutputs[imp].TaxAmount.TotalAmount  += Convert.ToDouble(factura.LineasFactura[i].ImpImpuesto.ToString("N8"));

                        insertado = true;
                    }
                }

                if (insertado == false)
                {
                    FacturaElectronicaV321.AmountType tAmountType = new FacturaElectronicaV321.AmountType();
                    FacturaElectronicaV321.AmountType bAmountType = new FacturaElectronicaV321.AmountType();
                    bAmountType.TotalAmount = Convert.ToDouble(factura.LineasFactura[i].CBase.ToString("N2"));
                    //Los impuestos se totalizan por tasa con maxima precision (8 decimales)
                    tAmountType.TotalAmount = Convert.ToDouble(factura.LineasFactura[i].ImpImpuesto.ToString("N8"));
                    FacturaElectronicaV321.TaxType oTax = new FacturaElectronicaV321.TaxType();
                    oTax.TaxableBase = bAmountType;
                    oTax.TaxAmount = bAmountType;
                    oTax.TaxTypeCode = FacturaElectronicaV321.TaxTypeCodeType.Item01;
                    oTax.TaxRate = Convert.ToDouble(factura.LineasFactura[i].PtjImpuesto);
                    FacturaElectronicaV321.TaxOutputType oTaxOutputType = new FacturaElectronicaV321.TaxOutputType()
                    {
                        TaxTypeCode = FacturaElectronicaV321.TaxTypeCodeType.Item01,
                        TaxRate = Convert.ToDouble(factura.LineasFactura[i].PtjImpuesto),
                        TaxableBase = bAmountType,
                        TaxAmount = tAmountType
                    };
                    totalFilasImpuesto++;

                    int countImp = 0;
                    for (countImp = 0; countImp < eFactura.Invoices[0].TaxesOutputs.Count(); countImp++)
                        if (eFactura.Invoices[0].TaxesOutputs[countImp] == null)
                            break;

                    eFactura.Invoices[0].TaxesOutputs[countImp] = oTaxOutputType;
                }
            }

            //Los impuestos se totalizan por tasa con maxima precision (8 decimales)
            //Una vez totalizado, redondeamos a 2
            foreach (FacturaElectronicaV321.TaxOutputType t in eFactura.Invoices[0].TaxesOutputs)
            {
                if (t != null &&  t.TaxAmount != null)
                    t.TaxAmount.TotalAmount = PrecisionV322(t.TaxAmount.TotalAmount);
            }

            decimal totalImpuestos = 0;
            foreach (cLineaFacturaBO linea in factura.LineasFactura)
                totalImpuestos += linea.ImpImpuesto;

            // //Totales
            FacturaElectronicaV321.InvoiceTotalsType oInvoiceTotals = new FacturaElectronicaV321.InvoiceTotalsType();
            oInvoiceTotals.InvoiceTotal = Convert.ToDouble(totalFacturado.ToString("N2"));
            oInvoiceTotals.TotalGrossAmount = Convert.ToDouble((Convert.ToDouble(totalFacturado.ToString("N2")) - Convert.ToDouble(totalImpuestos.ToString("N2"))).ToString("N2"));
            oInvoiceTotals.TotalGrossAmountBeforeTaxes = Convert.ToDouble((Convert.ToDouble(totalFacturado.ToString("N2")) - Convert.ToDouble(totalImpuestos.ToString("N2"))).ToString("N2"));
            oInvoiceTotals.TotalTaxOutputs = Convert.ToDouble(totalImpuestos.ToString("N2"));
            oInvoiceTotals.TotalOutstandingAmount = Convert.ToDouble(totalFacturado.ToString("N2"));
            oInvoiceTotals.TotalExecutableAmount = Convert.ToDouble(totalFacturado.ToString("N2"));
            oInvoiceTotals.TotalTaxesWithheld = 0.00;
            eFactura.Invoices[0].InvoiceTotals = oInvoiceTotals;

            // Lineas de factura
            int fila = 0; // Número de fila en el vector
            int numeroDeLineas = factura.LineasFactura.Count;
            eFactura.Invoices[0].Items = new FacturaElectronicaV321.InvoiceLineType[factura.LineasFactura.Count * 9]; // Número máximo de escalados
            for (int i = 0; i < numeroDeLineas && respuesta.Resultado == ResultadoProceso.OK; i++)
            {
                cTarifaBO tarifa = new cTarifaBO();
                tarifa.Codigo = factura.LineasFactura[i].CodigoTarifa;
                tarifa.CodigoServicio = factura.LineasFactura[i].CodigoServicio;
                cTarifaBL.Obtener(ref tarifa, out respuesta);
                if (respuesta.Resultado != ResultadoProceso.OK)
                    return respuesta;
                cTarifaBL.ObtenerServicio(ref tarifa, out respuesta);
                if (respuesta.Resultado != ResultadoProceso.OK)
                    return respuesta;

                FacturaElectronicaV321.InvoiceLineType oInvoiceLine;

                if (
                    (
                    (factura.LineasFactura[i].Precio != 0 && factura.LineasFactura[i].Unidades != 0)
                    // los iguiente para que no meta como cuota el minimo de soria inserso agua y depuracion
                    && (
                    (nombreExplotacion == "Soria") && (
                                                       ((factura.ContratoCodigo.Value != 31691 )    )                                                   
                    && (factura.LineasFactura[i].CodigoServicio != 1 || factura.LineasFactura[i].CodigoServicio != 2)
                                                        )
                    )
                    )

                    ||( (factura.LineasFactura[i].Precio != 0 && factura.LineasFactura[i].Unidades != 0)
                    // los iguiente para que no meta como cuota el minimo de soria inserso agua y depuracion
                    && (
                    (nombreExplotacion == "Soria") && (
                                                       ((factura.ContratoCodigo.Value != 31692))
                    && (factura.LineasFactura[i].CodigoServicio != 1 || factura.LineasFactura[i].CodigoServicio != 2)
                                                        )
                    )
                    )

                    //resto contratos no inserso
                    || ((factura.LineasFactura[i].Precio != 0 && factura.LineasFactura[i].Unidades != 0) && (nombreExplotacion == "Soria") && ((factura.ContratoCodigo.Value != 31691 || factura.ContratoCodigo.Value != 31692))
                       )
                    
                    //Resto Explo
                    || ((factura.LineasFactura[i].Precio != 0 && factura.LineasFactura[i].Unidades != 0) && (nombreExplotacion != "Soria"))
                    
                    )

                {
                    oInvoiceLine = new FacturaElectronicaV321.InvoiceLineType()
                    {
                        IssuerContractReference = factura.ContratoCodigo.ToString(),
                        IssuerTransactionDate = factura.Fecha.Value,
                        ReceiverContractReference = factura.ContratoCodigo.ToString(),
                        ArticleCode = tarifa.CodigoServicio.ToString() + "-" + tarifa.Codigo.ToString() + " - Cuota",
                        ItemDescription = tarifa.Servicio.Descripcion + " - " + tarifa.Descripcion,
                        Quantity = Convert.ToDouble(factura.LineasFactura[i].Unidades),
                        UnitOfMeasure = FacturaElectronicaV321.UnitOfMeasureType.Item01,
                        UnitOfMeasureSpecified = true,
                        UnitPriceWithoutTax = Convert.ToDouble(factura.LineasFactura[i].Precio),
                        TotalCost = Convert.ToDouble(factura.LineasFactura[i].ImporteCuota.ToString("N2")), //17-03-2016
                        GrossAmount = Convert.ToDouble(factura.LineasFactura[i].ImporteCuota.ToString("N2"))//17-03-2016
                    };

                    oInvoiceLine.TaxesOutputs = new FacturaElectronicaV321.InvoiceLineTypeTax[1];
                    oInvoiceLine.TaxesOutputs[0] = new FacturaElectronicaV321.InvoiceLineTypeTax();
                    oInvoiceLine.TaxesOutputs[0].TaxRate = Convert.ToDouble(factura.LineasFactura[i].PtjImpuesto);
                    oInvoiceLine.TaxesOutputs[0].TaxableBase = new FacturaElectronicaV321.AmountType();
                    oInvoiceLine.TaxesOutputs[0].TaxableBase.TotalAmount = Convert.ToDouble((factura.LineasFactura[i].Precio * factura.LineasFactura[i].Unidades).ToString("N2"));//17-03-2016

                    eFactura.Invoices[0].Items[fila] = oInvoiceLine;
                }

                double dQuantity;
                double sUnitPriceWithoutTax;
                bool insertoMinimoSoria = false;
                if ((tarifa.Servicio.Tipo == "M") || // Solo para servicios medidos O SORIA INSERSO
                     (
                    (nombreExplotacion == "Soria") && ((factura.ContratoCodigo.Value == 31691 || factura.ContratoCodigo.Value == 31692))
                    ) && (factura.LineasFactura[i].CodigoServicio == 1 || factura.LineasFactura[i].CodigoServicio == 2)
                    
                    )
                {
                    for (int c = 0; c < 9; c++)  // Insertar tantas filas como escalados utilizados tenga la línea de factura
                    {

                        /////////////////
                        // si es Soria cuota y soria y no inserté ya
                        if (
                    (nombreExplotacion == "Soria") && ((factura.ContratoCodigo.Value == 31691 || factura.ContratoCodigo.Value == 31692))
                    && (factura.LineasFactura[i].CodigoServicio == 1 || factura.LineasFactura[i].CodigoServicio == 2)
                    && (insertoMinimoSoria == false) && factura.LineasFactura[i].Precio != 0
                    )
                        {
                            dQuantity = Convert.ToDouble(factura.LineasFactura[i].ArrayEscalas[0]);
                            //round(Fields!fclPrecio.Value / (Fields!fclEscala1.Value / Fields!fclUnidades.Value), 6, System.MidpointRounding.AwayFromZero),""))
                            sUnitPriceWithoutTax = Convert.ToDouble((factura.LineasFactura[i].Precio / factura.LineasFactura[i].ArrayEscalas[0] / factura.LineasFactura[i].Unidades));

                            // Líneas de Minimos Soria inserso

                            oInvoiceLine = new FacturaElectronicaV321.InvoiceLineType()
                            {
                                IssuerContractReference = factura.ContratoCodigo.ToString(),
                                IssuerTransactionDate = factura.Fecha.Value,
                                ReceiverContractReference = factura.ContratoCodigo.ToString(),
                                ArticleCode = tarifa.CodigoServicio.ToString() + "-" + tarifa.Codigo.ToString() + " - Consumo",
                                ItemDescription = tarifa.Servicio.Descripcion + " - " + tarifa.Descripcion,
                                Quantity = dQuantity,
                                UnitOfMeasure = FacturaElectronicaV321.UnitOfMeasureType.Item01,
                                UnitOfMeasureSpecified = true,
                                UnitPriceWithoutTax = sUnitPriceWithoutTax,
                                TotalCost = Convert.ToDouble(factura.LineasFactura[i].ImporteCuota.ToString("N2")), //17-03-2016
                                GrossAmount = Convert.ToDouble(factura.LineasFactura[i].ImporteCuota.ToString("N2"))//17-03-2016

                            };

                            oInvoiceLine.TaxesOutputs = new FacturaElectronicaV321.InvoiceLineTypeTax[1];
                            oInvoiceLine.TaxesOutputs[0] = new FacturaElectronicaV321.InvoiceLineTypeTax();
                            oInvoiceLine.TaxesOutputs[0].TaxRate = Convert.ToDouble(factura.LineasFactura[i].PtjImpuesto);
                            oInvoiceLine.TaxesOutputs[0].TaxableBase = new FacturaElectronicaV321.AmountType();
                            oInvoiceLine.TaxesOutputs[0].TaxableBase.TotalAmount = Convert.ToDouble((factura.LineasFactura[i].Precio * factura.LineasFactura[i].Unidades).ToString("N2"));//17-03-2016

                            eFactura.Invoices[0].Items[fila] = oInvoiceLine;

                            insertoMinimoSoria = true;
                        } //Fin soria inserso


                        if (factura.LineasFactura[i].ArrayUnidades[c] != 0 && factura.LineasFactura[i].ArrayPrecios[c] != 0)
                        {
                            // Líneas de escalados
                            eFactura.Invoices[0].Items[fila + 1] = CreateInvoiceLineForServiceMeasured(factura, tarifa, factura.LineasFactura[i], c);
                            fila++;
                        }
                    }
                }
                fila++;
            }

            cOficinaContableBO ofiCon = cOficinasContablesBL.Obtener(contrato.FacturaeOficinaContable, out respuesta);
            bool oficinasConExtensiones = (respuesta.Resultado == ResultadoProceso.OK && ofiCon.EnviarConExtensiones.HasValue) ? ofiCon.EnviarConExtensiones.Value : false;

            if (factura.PeriodoCodigo.Substring(0, 1) != "0")
            {
                // Extensión de la factura electrónica
                FacturaElectronicaV321.UtilitiesExtension UtilitiesExtension = new FacturaElectronicaV321.UtilitiesExtension()
                {
                    Version = "1.0",
                };

                cContadorBO contadorInstalado = cCtrConBL.ObtenerUltimoContadorInstalado(factura.ContratoCodigo.Value, out respuesta);
                if (respuesta.Resultado == ResultadoProceso.Error)
                    return respuesta;
                respuesta.Resultado = ResultadoProceso.OK;

                // Datos de suministro
                FacturaElectronicaV321.DatosDelSuministroType datosSuministro = new FacturaElectronicaV321.DatosDelSuministroType();
                datosSuministro.CUPS = contadorInstalado != null ? contadorInstalado.NumSerie : String.Empty;
                datosSuministro.Contrato = new FacturaElectronicaV321.ContratoType();
                datosSuministro.Contrato.RefContratoEmpresa = factura.ContratoCodigo.ToString();
                datosSuministro.Contrato.ReferenciaPropiaCliente = contrato.TitularCodigo.ToString();
                datosSuministro.Distribuidora = sociedad.Nombre;

                cContratoBL.ObtenerInmueble(ref contrato, out respuesta);
                if (respuesta.Resultado != ResultadoProceso.OK)
                    return respuesta;

                inmueble = contrato.InmuebleBO;
                respuesta = cInmuebleBL.ObtenerPoblacion(ref inmueble);
                if (respuesta.Resultado != ResultadoProceso.OK)
                    return respuesta;

                respuesta = cInmuebleBL.ObtenerProvincia(ref inmueble);
                if (respuesta.Resultado != ResultadoProceso.OK)
                    return respuesta;

                datosSuministro.DireccionSuministro = new FacturaElectronicaV321.DireccionSuministroType();
                datosSuministro.DireccionSuministro.Direccion = contrato.InmuebleBO.Direccion;
                datosSuministro.DireccionSuministro.CodigoPostal = contrato.InmuebleBO.CodigoPostal;
                datosSuministro.DireccionSuministro.Poblacion = inmueble.Poblacion.Descripcion;
                datosSuministro.DireccionSuministro.Provincia = inmueble.Provincia.Descripcion;
                datosSuministro.DireccionSuministro.Pais = "ESP";
                datosSuministro.DireccionSuministro.RefCatastral = contrato.InmuebleBO.RefCatastral;

                eFactura.Invoices[0].AdditionalData = new FacturaElectronicaV321.AdditionalDataType();
                eFactura.Invoices[0].AdditionalData.InvoiceAdditionalInformation =
                " Dirección de suministro: " + contrato.InmuebleBO.Direccion + "\n" +
                contrato.InmuebleBO.CodigoPostal + " - " + inmueble.Poblacion.Descripcion + " - " + inmueble.Provincia.Descripcion + "\n";
                eFactura.Invoices[0].AdditionalData.InvoiceAdditionalInformation +=
                " Fecha y lectura anterior: " + (factura.FechaLecturaAnterior.HasValue ? factura.FechaLecturaAnterior.Value.ToShortDateString() + "-" : String.Empty) + factura.LecturaAnterior.ToString() + "m3\n" +
                " Fecha y lectura actual: " + (factura.FechaLecturaFactura.HasValue ? factura.FechaLecturaFactura.Value.ToShortDateString() + "-" : String.Empty) + factura.LecturaFactura.ToString() + "m3\n" +
                " Consumo: " + (factura.ConsumoFactura.HasValue ? factura.ConsumoFactura.Value : 0).ToString() + "m3\n";
                eFactura.Invoices[0].AdditionalData.InvoiceAdditionalInformation += " Total: " + (factura.TotalFacturado.HasValue ? factura.TotalFacturado.Value.ToString("N2") : "0").ToString();
                eFactura.Invoices[0].AdditionalData.InvoiceAdditionalInformation += incidencia.MCalculo == "E" || incidencia.MCalculo == "EP" ? "Estimada\n" : String.Empty;

                UtilitiesExtension.DatosDelSuministro = datosSuministro;

                if (oficinasConExtensiones)
                {
                    short aguaCodigo;
                    respuesta = cParametroBL.GetShort("SERVICIO_AGUA", out aguaCodigo);
                    if (respuesta.Resultado != ResultadoProceso.OK)
                        return respuesta;

                    cLineaFacturaBO lineaAgua = new cLineaFacturaBO();
                    lineaAgua.CodigoServicio = aguaCodigo;
                    lineaAgua.Contrato = factura.ContratoCodigo.HasValue ? factura.ContratoCodigo.Value : 0;
                    lineaAgua.Periodo = factura.PeriodoCodigo;
                    lineaAgua.Version = factura.Version.HasValue ? factura.Version.Value : (short)0;
                    lineaAgua.FacturaCodigo = factura.FacturaCodigo.HasValue ? factura.FacturaCodigo.Value : (short)0;

                    new cLineasFacturaBL().Obtener(ref lineaAgua, out respuesta);

                    if (respuesta.Resultado != ResultadoProceso.OK)
                    {
                        if (respuesta.Resultado == ResultadoProceso.Error)
                            return respuesta;
                        else
                            respuesta.Resultado = ResultadoProceso.OK;
                    }
                    else
                    {
                        cTarvalBO tarVal = new cTarvalBO();
                        tarVal.Codigo = lineaAgua.CodigoTarifa;
                        tarVal.CodigoServicio = lineaAgua.CodigoServicio;
                        cTarvalBL.Obtener(ref tarVal, out respuesta);

                        if (respuesta.Resultado == ResultadoProceso.Error)
                            return respuesta;
                        respuesta.Resultado = ResultadoProceso.OK;
                        if (respuesta.Resultado == ResultadoProceso.OK)
                        {
                            datosSuministro.ReferenciaLegal = new FacturaElectronicaV321.ReferenciaLegalType();
                            datosSuministro.ReferenciaLegal.BOEBOCA = tarVal.LegalAvb;
                        }

                        if (contrato != null && contrato.UsoCodigo.HasValue)
                        {
                            datosSuministro.Usos = new FacturaElectronicaV321.UsosType();
                            if (contrato.UsoCodigo == 1) // Doméstico = 1
                                datosSuministro.Usos.NumeroViviendas = Convert.ToInt32(lineaAgua.Unidades).ToString();
                            else
                                datosSuministro.Usos.NumeroLocales = Convert.ToInt32(lineaAgua.Unidades).ToString();
                        }
                    }

                    // Si la factura es domiciliada
                    if (!String.IsNullOrEmpty(contrato.Iban) && !String.IsNullOrEmpty(contrato.Bic))
                    {
                        cBicBO bic = cBicBL.Obtener(contrato.Bic, out respuesta);
                        if (respuesta.Resultado != ResultadoProceso.OK)
                            return respuesta;
                        datosSuministro.NombreBanco = bic.Nombre;
                        datosSuministro.TitularBancario = !String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularNombre : contrato.PagadorNombre;
                    }

                    datosSuministro.OrigenFactura = "ES";
                    datosSuministro.IDDocumento = codigoExplotacion + "-" + factura.SerieCodigo.ToString() + factura.Numero.ToString();
                    datosSuministro.TotalAPagar = Convert.ToDouble(factura.TotalFacturado.HasValue ? factura.TotalFacturado.Value : 0);

                    // Medidas
                    FacturaElectronicaV321.UtilitiesMedidaType medidas = new FacturaElectronicaV321.UtilitiesMedidaType();
                    medidas.MedidasSobreEquipo = new FacturaElectronicaV321.MedidaSobreEquipoType[1];
                    medidas.MedidasSobreEquipo[0] = new FacturaElectronicaV321.MedidaSobreEquipoType();
                    //medidas.MedidasSobreEquipo[0].Calibre = contadorInstalado.Diametro.ToString();
                    medidas.MedidasSobreEquipo[0].LecturaDesdeSpecified = true;
                    medidas.MedidasSobreEquipo[0].LecturaDesde = factura.LecturaAnterior;
                    medidas.MedidasSobreEquipo[0].LecturaHastaSpecified = true;
                    medidas.MedidasSobreEquipo[0].LecturaHasta = factura.LecturaFactura;

                    incidencia.Codigo = String.IsNullOrEmpty(factura.InspectorIncidenciaLectura) ? (String.IsNullOrEmpty(factura.LectorIncidenciaLectura) ? null : factura.LectorIncidenciaLectura) : factura.InspectorIncidenciaLectura;

                    if (!String.IsNullOrEmpty(incidencia.Codigo))
                    {
                        new cIncilecBL().Obtener(ref incidencia, out respuesta);
                        if (respuesta.Resultado != ResultadoProceso.OK)
                            return respuesta;
                    }

                    medidas.MedidasSobreEquipo[0].TipoDeLecturaActual = incidencia != null && (incidencia.MCalculo == "E" || incidencia.MCalculo == "EP") ? "Estimado" : "Leido";
                    if (medidas.MedidasSobreEquipo[0].TipoDeLecturaActual != "Estimada")
                    {
                        medidas.MedidasSobreEquipo[0].ConsumoLeidoSpecified = true;
                        medidas.MedidasSobreEquipo[0].ConsumoLeido = factura.ConsumoFactura.HasValue ? factura.ConsumoFactura.Value : 0;
                    }
                    else
                    {
                        medidas.MedidasSobreEquipo[0].ConsumoCalculadoSpecified = true;
                        medidas.MedidasSobreEquipo[0].ConsumoCalculado = factura.ConsumoFactura.HasValue ? factura.ConsumoFactura.Value : 0;
                    }

                    cFacturasBL.ObtenerPeriodo(ref factura, out respuesta);
                    if (respuesta.Resultado != ResultadoProceso.OK)
                        return respuesta;

                    // Histórico de consumos
                    FacturaElectronicaV321.HistoricoConsumoType[] historicos = new FacturaElectronicaV321.HistoricoConsumoType[2];
                    historicos[0] = new FacturaElectronicaV321.HistoricoConsumoType();
                    historicos[0].Periodo = factura.PeriodoCodigo;
                    historicos[0].Descripcion = factura.Periodo.Descripcion;
                    historicos[0].ValorSpecified = true;
                    historicos[0].Valor = factura.ConsumoFactura.HasValue ? factura.ConsumoFactura.Value : 0;
                    historicos[0].UnidadMedida = "m3";
                    historicos[0].FechaIniPeriodoSpecified = true;
                    historicos[0].FechaIniPeriodo = factura.FechaLecturaAnterior;
                    historicos[0].FechaFinPeriodoSpecified = true;
                    historicos[0].FechaFinPeriodo = factura.FechaLecturaFactura;
                    historicos[0].TipoCalculo = "Exacto";

                    cFacturaBO facturaAnterior = new cFacturaBO();
                    facturaAnterior.PeriodoCodigo = new cPeriodoBL().ObtenerPeriodoConsumoAnterior(factura.PeriodoCodigo, out respuesta);
                    facturaAnterior.ContratoCodigo = factura.ContratoCodigo;

                    if (respuesta.Resultado == ResultadoProceso.Error)
                        return respuesta;

                    if (respuesta.Resultado == ResultadoProceso.OK)
                    {
                        cFacturasBL.Obtener(ref facturaAnterior, out respuesta);

                        if (respuesta.Resultado != ResultadoProceso.OK)
                            return respuesta;

                        historicos[1] = new FacturaElectronicaV321.HistoricoConsumoType();
                        historicos[1].Periodo = facturaAnterior.PeriodoCodigo;
                        historicos[1].Descripcion = facturaAnterior.Periodo.Descripcion;
                        historicos[1].ValorSpecified = true;
                        historicos[1].Valor = facturaAnterior.ConsumoFactura.HasValue ? facturaAnterior.ConsumoFactura.Value : 0;
                        historicos[1].UnidadMedida = "m3";
                        historicos[1].FechaIniPeriodoSpecified = true;
                        historicos[1].FechaIniPeriodo = facturaAnterior.FechaLecturaAnterior;
                        historicos[1].FechaFinPeriodoSpecified = true;
                        historicos[1].FechaFinPeriodo = facturaAnterior.FechaLecturaFactura;
                        historicos[1].TipoCalculo = "Exacto";
                    }

                    respuesta.Resultado = ResultadoProceso.OK;

                    // Datos adicionales
                    string[] datosAdicionales = new string[1];
                    string ibanOculto = "INGRESO EN CUENTA DE " + nombreExplotacion;
                    if (!String.IsNullOrEmpty(contrato.Iban))
                    {
                        if (contrato.Iban.Length > 34 || contrato.Iban.Length < 24)
                            ibanOculto = "INGRESO EN CUENTA DE " + nombreExplotacion;
                        else
                            ibanOculto = "Será cargada en: " + contrato.Iban.Substring(0, 12) + "********" + contrato.Iban.Substring(20);
                    }

                    datosAdicionales[0] = ibanOculto + " ";

                    UtilitiesExtension.UtilitiesMedida = medidas;
                    UtilitiesExtension.UtilitiesHistoricoConsumos = historicos;
                    UtilitiesExtension.DatosPagoAdicionales = datosAdicionales;

                    XmlDocument xmld2 = new XmlDocument();

                    //XmlSerializerNamespaces namespaces = new XmlSerializerNamespaces();
                    //namespaces.Add("ex", "http://www.facturae.es/Facturae/Extensions/Utilities");

                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (XmlTextWriter xmltw = new XmlTextWriter(ms, Encoding.UTF8))
                        {
                            XmlSerializer xmlser = new XmlSerializer(typeof(FacturaElectronicaV321.UtilitiesExtension));
                            //xmlser.Serialize(xmltw, UtilitiesExtension, namespaces);
                            xmlser.Serialize(xmltw, UtilitiesExtension);
                            ms.Seek(0, SeekOrigin.Begin);
                            xmld2.Load(ms);
                        }
                    }
                    eFactura.Invoices[0].AdditionalData.Extensions = new FacturaElectronicaV321.ExtensionsType();
                    eFactura.Invoices[0].AdditionalData.Extensions.Any = new XmlElement[] { xmld2.DocumentElement };
                }
            }

            // Actualizar el campo referente al momento en el que se encuentra el proceso SERES
            if (respuesta.Resultado == ResultadoProceso.OK)
            {
                factura.EnvSERES = "E";
                string log = String.Empty;

                cFacturasBL.Actualizar(factura, false, out log, out respuesta);
            }

            if (respuesta.Resultado == ResultadoProceso.OK)
                facturaXML = Serializar(eFactura);

            return respuesta;
        }

        //****************************************************************
        //COMPLEJIDAD EXCESIVA DEL METODO casi 800 Lineas!!!!
        public static cRespuesta GenerarFacturaV321(cFacturaBO factura, out XmlDocument facturaXML)
        {
            cRespuesta respuesta = new cRespuesta();
            FacturaElectronicaV321.Facturae eFactura = new FacturaElectronicaV321.Facturae();
            facturaXML = new XmlDocument();

            cInmuebleBO inmueble = new cInmuebleBO();
            cIncilecBO incidencia = new cIncilecBO();

            short sociedadCodigo;
            cSociedadBO sociedad = new cSociedadBO();
            respuesta = cParametroBL.GetShort("SOCIEDAD_POR_DEFECTO", out sociedadCodigo);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            string bicElectronico;
            respuesta = cParametroBL.GetString("BIC_ELECTRONICO", out bicElectronico);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;
            string ibanElectronico;
            respuesta = cParametroBL.GetString("IBAN_ELECTRONICO", out ibanElectronico);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            string diasPagoVoluntario;
            respuesta = cParametroBL.GetString("DIAS_PAGO_VOLUNTARIO", out diasPagoVoluntario);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            sociedad.Codigo = sociedadCodigo;
            cSociedadBL.Obtener(ref sociedad, out respuesta);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            cFacturasBL.ObtenerLineas(ref factura, out respuesta);
            if (respuesta.Resultado == ResultadoProceso.OK)
                cFacturasBL.ObtenerImporteFacturado(ref factura, null);
            else
                return respuesta;

            string codigoExplotacion;
            respuesta = cParametroBL.GetString("EXPLOTACION_CODIGO", out codigoExplotacion);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;
            else
                codigoExplotacion = cAplicacion.FixedLengthString(Convert.ToInt32(codigoExplotacion).ToString(), 3, '0', false, false);

            decimal totalFacturadoTodas = factura.TotalFacturado.HasValue ? factura.TotalFacturado.Value : 0;

            eFactura.FileHeader = new FacturaElectronicaV321.FileHeaderType();

            eFactura.FileHeader.Batch = new FacturaElectronicaV321.BatchType();
            eFactura.FileHeader.Batch.InvoicesCount = 1;
            eFactura.FileHeader.Batch.BatchIdentifier = "ES" + sociedad.Nif + factura.Numero.ToString() + "-" + codigoExplotacion + "-" + factura.SerieCodigo.ToString();
            eFactura.FileHeader.Batch.TotalInvoicesAmount = new FacturaElectronicaV321.AmountType();
            eFactura.FileHeader.Batch.TotalInvoicesAmount.TotalAmount = Convert.ToDouble(totalFacturadoTodas.ToString("N2"));
            eFactura.FileHeader.Batch.TotalOutstandingAmount = new FacturaElectronicaV321.AmountType();
            eFactura.FileHeader.Batch.TotalOutstandingAmount.TotalAmount = Convert.ToDouble(totalFacturadoTodas.ToString("N2"));
            eFactura.FileHeader.Batch.TotalExecutableAmount = new FacturaElectronicaV321.AmountType();
            eFactura.FileHeader.Batch.TotalExecutableAmount.TotalAmount = Convert.ToDouble(totalFacturadoTodas.ToString("N2"));
            eFactura.FileHeader.Batch.InvoiceCurrencyCode = new FacturaElectronicaV321.CurrencyCodeType();
            eFactura.FileHeader.Batch.InvoiceCurrencyCode = FacturaElectronicaV321.CurrencyCodeType.EUR;

            eFactura.Parties = new FacturaElectronicaV321.PartiesType();

            eFactura.Parties.SellerParty = new FacturaElectronicaV321.BusinessType();
            eFactura.Parties.SellerParty.TaxIdentification = new FacturaElectronicaV321.TaxIdentificationType();
            eFactura.Parties.SellerParty.TaxIdentification.PersonTypeCode = FacturaElectronicaV321.PersonTypeCodeType.J;
            eFactura.Parties.SellerParty.TaxIdentification.ResidenceTypeCode = FacturaElectronicaV321.ResidenceTypeCodeType.R;
            eFactura.Parties.SellerParty.TaxIdentification.TaxIdentificationNumber = "ES" + sociedad.Nif;

            string cb;
            respuesta = cParametroBL.GetString("CB_SERES", out cb);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            FacturaElectronicaV321.AddressType bAddressType = new FacturaElectronicaV321.AddressType();
            if (!String.IsNullOrEmpty(sociedad.Nacion))
                bAddressType.CountryCode = (FacturaElectronicaV321.CountryType)Enum.Parse(typeof(FacturaElectronicaV321.CountryType), sociedad.Nacion);
            bAddressType.Address = sociedad.Domicilio;
            bAddressType.PostCode = sociedad.CPostal;
            bAddressType.Province = sociedad.Provincia;
            bAddressType.Town = sociedad.Poblacion;

            string nombreExplotacion;
            respuesta = cParametroBL.GetString("EXPLOTACION", out nombreExplotacion);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            eFactura.Parties.SellerParty.AdministrativeCentres = new FacturaElectronicaV321.AdministrativeCentreType[1];
            eFactura.Parties.SellerParty.AdministrativeCentres[0] = new FacturaElectronicaV321.AdministrativeCentreType();
            eFactura.Parties.SellerParty.AdministrativeCentres[0].RoleTypeCodeSpecified = true;
            eFactura.Parties.SellerParty.AdministrativeCentres[0].RoleTypeCode = FacturaElectronicaV321.RoleTypeCodeType.Item09;
            eFactura.Parties.SellerParty.AdministrativeCentres[0].CentreCode = cb;
            eFactura.Parties.SellerParty.AdministrativeCentres[0].Name = nombreExplotacion;
            eFactura.Parties.SellerParty.AdministrativeCentres[0].Item = bAddressType;

            FacturaElectronicaV321.LegalEntityType bEntidadType = new FacturaElectronicaV321.LegalEntityType();
            bEntidadType.CorporateName = sociedad.Nombre;
            bEntidadType.TradeName = sociedad.Nombre;
            bEntidadType.ContactDetails = new FacturaElectronicaV321.ContactDetailsType();
            bEntidadType.ContactDetails.Telephone = sociedad.Telefono1;
            bEntidadType.ContactDetails.ElectronicMail = sociedad.Email;

            bEntidadType.Item = bAddressType;
            eFactura.Parties.SellerParty.Item = bEntidadType;

            eFactura.Invoices = new FacturaElectronicaV321.InvoiceType[1];

            cContratoBO contrato = new cContratoBO();
            contrato.Codigo = factura.ContratoCodigo.Value;
            contrato.Version = factura.ContratoVersion.Value;
            cContratoBL.Obtener(ref contrato, out respuesta);
            factura.Contrato = contrato;

            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            eFactura.Parties.BuyerParty = new FacturaElectronicaV321.BusinessType();

            // Obtenemos el ISO 2 del país del pagador
            cCatalogoPaisBO catalogoPais = cCatalogosPaisesBL.Obtener(String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularNacion : contrato.PagadorNacion, out respuesta);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            eFactura.Parties.BuyerParty.TaxIdentification = new FacturaElectronicaV321.TaxIdentificationType();
            eFactura.Parties.BuyerParty.TaxIdentification.PersonTypeCode = cVarios.ValidateCIF(String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularDocIden : contrato.PagadorDocIden) ? FacturaElectronicaV321.PersonTypeCodeType.J : FacturaElectronicaV321.PersonTypeCodeType.F;
            eFactura.Parties.BuyerParty.TaxIdentification.TaxIdentificationNumber = catalogoPais.ISOAlfa2 + (String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularDocIden : contrato.PagadorDocIden);
            eFactura.Parties.BuyerParty.TaxIdentification.ResidenceTypeCode = FacturaElectronicaV321.ResidenceTypeCodeType.R;

            // Dirección de la persona jurídica/fisica de la factura
            FacturaElectronicaV321.AddressType oAddressType = new FacturaElectronicaV321.AddressType();
            if (!String.IsNullOrEmpty(String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularNacion : contrato.PagadorNacion))
                oAddressType.CountryCode = (FacturaElectronicaV321.CountryType)Enum.Parse(typeof(FacturaElectronicaV321.CountryType), String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularNacion : contrato.PagadorNacion);
            oAddressType.Address = String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularDireccion : contrato.PagadorDireccion;
            oAddressType.PostCode = String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularCpostal : contrato.PagadorCpostal;
            oAddressType.Province = String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularProvincia : contrato.PagadorProvincia;
            oAddressType.Town = String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularPoblacion : contrato.PagadorPoblacion;

            eFactura.Parties.BuyerParty.AdministrativeCentres = new FacturaElectronicaV321.AdministrativeCentreType[4];
            eFactura.Parties.BuyerParty.AdministrativeCentres[0] = new FacturaElectronicaV321.AdministrativeCentreType();
            eFactura.Parties.BuyerParty.AdministrativeCentres[0].CentreCode = contrato.FacturaeOficinaContable;
            eFactura.Parties.BuyerParty.AdministrativeCentres[0].RoleTypeCodeSpecified = true;
            eFactura.Parties.BuyerParty.AdministrativeCentres[0].RoleTypeCode = FacturaElectronicaV321.RoleTypeCodeType.Item01;
            eFactura.Parties.BuyerParty.AdministrativeCentres[0].Item = oAddressType;
            eFactura.Parties.BuyerParty.AdministrativeCentres[0].CentreDescription = "Oficina contable";

            eFactura.Parties.BuyerParty.AdministrativeCentres[1] = new FacturaElectronicaV321.AdministrativeCentreType();
            eFactura.Parties.BuyerParty.AdministrativeCentres[1].CentreCode = contrato.FacturaeOrganismoGestor;
            eFactura.Parties.BuyerParty.AdministrativeCentres[1].RoleTypeCodeSpecified = true;
            eFactura.Parties.BuyerParty.AdministrativeCentres[1].RoleTypeCode = FacturaElectronicaV321.RoleTypeCodeType.Item02;
            eFactura.Parties.BuyerParty.AdministrativeCentres[1].Item = oAddressType;
            eFactura.Parties.BuyerParty.AdministrativeCentres[1].CentreDescription = "Órgano Gestor";

            eFactura.Parties.BuyerParty.AdministrativeCentres[2] = new FacturaElectronicaV321.AdministrativeCentreType();
            eFactura.Parties.BuyerParty.AdministrativeCentres[2].CentreCode = contrato.FacturaeUnidadTramitadora;
            eFactura.Parties.BuyerParty.AdministrativeCentres[2].RoleTypeCodeSpecified = true;
            eFactura.Parties.BuyerParty.AdministrativeCentres[2].RoleTypeCode = FacturaElectronicaV321.RoleTypeCodeType.Item03;
            eFactura.Parties.BuyerParty.AdministrativeCentres[2].Item = oAddressType;
            eFactura.Parties.BuyerParty.AdministrativeCentres[2].CentreDescription = "Unidad Tramitadora";

            eFactura.Parties.BuyerParty.AdministrativeCentres[3] = new FacturaElectronicaV321.AdministrativeCentreType();
            if (!String.IsNullOrEmpty(contrato.FacturaeOrganoProponente))
                eFactura.Parties.BuyerParty.AdministrativeCentres[3].CentreCode = contrato.FacturaeOrganoProponente;
            eFactura.Parties.BuyerParty.AdministrativeCentres[3].RoleTypeCodeSpecified = true;
            eFactura.Parties.BuyerParty.AdministrativeCentres[3].RoleTypeCode = FacturaElectronicaV321.RoleTypeCodeType.Item04;
            eFactura.Parties.BuyerParty.AdministrativeCentres[3].Item = oAddressType;
            eFactura.Parties.BuyerParty.AdministrativeCentres[3].CentreDescription = "Subdirección de compras";

            if (cVarios.ValidateCIF(String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularDocIden : contrato.PagadorDocIden))
            {
                FacturaElectronicaV321.LegalEntityType personaJuridica = new FacturaElectronicaV321.LegalEntityType();

                personaJuridica.CorporateName = String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularNombre : contrato.PagadorNombre;
                personaJuridica.ContactDetails = new FacturaElectronicaV321.ContactDetailsType();
                personaJuridica.ContactDetails.Telephone = contrato.Telefono1;
                personaJuridica.ContactDetails.ElectronicMail = contrato.Email;

                personaJuridica.Item = oAddressType;
                eFactura.Parties.BuyerParty.Item = personaJuridica;
            }
            else
            {
                FacturaElectronicaV321.IndividualType personaFisica = new FacturaElectronicaV321.IndividualType();
                personaFisica.Name = String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularNombre : contrato.PagadorNombre;
                personaFisica.FirstSurname = String.Empty;
                personaFisica.ContactDetails = new FacturaElectronicaV321.ContactDetailsType();
                personaFisica.ContactDetails.Telephone = contrato.Telefono1;
                personaFisica.ContactDetails.ElectronicMail = contrato.Email;

                personaFisica.Item = oAddressType;
                eFactura.Parties.BuyerParty.Item = personaFisica;
            }

            FacturaElectronicaV321.PlaceOfIssueType oPlaceOfIssueType = new FacturaElectronicaV321.PlaceOfIssueType();// Lugar donde se emite la factura
            oPlaceOfIssueType.PlaceOfIssueDescription = sociedad.Poblacion;
            oPlaceOfIssueType.PostCode = sociedad.CPostal;

            cPerzonaBO perzona = new cPerzonaBO();
            if (factura.PeriodoCodigo.Substring(0, 1) != "0")
            {
                perzona.CodigoPeriodo = factura.PeriodoCodigo;
                perzona.CodigoZona = factura.ZonaCodigo;
                new cPerzonaBL().Obtener(ref perzona, out respuesta);
                if (respuesta.Resultado != ResultadoProceso.OK || !perzona.FPeriodoDesde.HasValue || !perzona.FPeriodoHasta.HasValue)
                    return respuesta;
            }

            // Periodo de facturación
            FacturaElectronicaV321.PeriodDates oPeriodDates = new FacturaElectronicaV321.PeriodDates();
            oPeriodDates.StartDate = factura.PeriodoCodigo.Substring(0, 1) != "0" ? perzona.FPeriodoDesde.Value : new DateTime(factura.Fecha.Value.Year, factura.Fecha.Value.Month, 1);
            oPeriodDates.EndDate = factura.PeriodoCodigo.Substring(0, 1) != "0" ? perzona.FPeriodoHasta.Value : new DateTime(factura.Fecha.Value.Year, factura.Fecha.Value.Month, DateTime.DaysInMonth(factura.Fecha.Value.Year, factura.Fecha.Value.Month));

            //((XmlEnumAttribute)typeof(ReasonCodeType) factura.RazRectcod

            FacturaElectronicaV321.CorrectiveType rectificativa = null;
            if (factura.Version > 1)
            {

                respuesta = cFacturasBL.ObtenerRectificada(ref factura);
                if (respuesta.Resultado != ResultadoProceso.OK)
                    return respuesta;

                var razonCodigo = GetEnumValue<FacturaElectronicaV321.ReasonCodeType>(factura.RazRectcod);
                var razonDescripcion = GetEnumValue<FacturaElectronicaV321.ReasonDescriptionType>(factura.RazRectDescType);
                var metodoCorreccion = GetEnumValue<FacturaElectronicaV321.CorrectionMethodType>(factura.MeRect);
                var metodoDescripcion = GetEnumValue<FacturaElectronicaV321.CorrectionMethodDescriptionType>(factura.MeRectType);

                rectificativa = new FacturaElectronicaV321.CorrectiveType()
                {
                    InvoiceNumber = !String.IsNullOrEmpty(factura.FacturaRectificada.Numero) ? factura.FacturaRectificada.Numero.ToString() : String.Empty,
                    InvoiceSeriesCode = factura.FacturaRectificada.SerieCodigo.HasValue ? codigoExplotacion + "-" + factura.FacturaRectificada.SerieCodigo.ToString() : String.Empty,

                    ReasonCode = razonCodigo,
                    ReasonDescription = razonDescripcion,

                    TaxPeriod = new FacturaElectronicaV321.PeriodDates()
                    {
                        StartDate = factura.PeriodoCodigo.Substring(0, 1) != "0" ? perzona.FPeriodoDesde.Value : new DateTime(factura.Fecha.Value.Year, factura.Fecha.Value.Month, 1),
                        EndDate = factura.PeriodoCodigo.Substring(0, 1) != "0" ? perzona.FPeriodoHasta.Value : new DateTime(factura.Fecha.Value.Year, factura.Fecha.Value.Month, DateTime.DaysInMonth(factura.Fecha.Value.Year, factura.Fecha.Value.Month)),
                    },

                    CorrectionMethod = metodoCorreccion,
                    CorrectionMethodDescription = metodoDescripcion,
                };
            }

            FacturaElectronicaV321.InvoiceType facturaIndividual =
                new FacturaElectronicaV321.InvoiceType()
                {
                    InvoiceHeader = new FacturaElectronicaV321.InvoiceHeaderType()
                    {
                        InvoiceNumber = factura.Numero.ToString(),
                        InvoiceSeriesCode = codigoExplotacion + "-" + factura.SerieCodigo.ToString(),
                        InvoiceDocumentType = FacturaElectronicaV321.InvoiceDocumentTypeType.FC,
                        InvoiceClass = factura.Version == 1 ? FacturaElectronicaV321.InvoiceClassType.OO : FacturaElectronicaV321.InvoiceClassType.OR, // Original o Rectificativa
                        Corrective = rectificativa,
                    },

                    InvoiceIssueData = new FacturaElectronicaV321.InvoiceIssueDataType()
                    {
                        IssueDate = factura.Fecha.Value,
                        PlaceOfIssue = oPlaceOfIssueType,
                        InvoicingPeriod = oPeriodDates,
                        InvoiceCurrencyCode = FacturaElectronicaV321.CurrencyCodeType.EUR,
                        TaxCurrencyCode = FacturaElectronicaV321.CurrencyCodeType.EUR,
                        LanguageName = FacturaElectronicaV321.LanguageCodeType.es,
                    }
                };

            if (/*contrato.FacturaePortal == cContratoBO.EFacturaePortal.JuntaCastillaMancha &&*/ String.IsNullOrEmpty(contrato.Iban))
            {
                FacturaElectronicaV321.InstallmentType detallesDelPago = new FacturaElectronicaV321.InstallmentType()
                {
                    PaymentMeans = FacturaElectronicaV321.PaymentMeansType.Item04,

                    AccountToBeCredited = new FacturaElectronicaV321.AccountType()
                    {

                        ItemElementName = FacturaElectronicaV321.ItemChoiceType.IBAN,
                        Item = ibanElectronico,
                        BIC = bicElectronico
                    }
                };

                detallesDelPago.InstallmentAmount = totalFacturadoTodas.ToString();
                detallesDelPago.InstallmentDueDate = factura.Fecha.Value.AddDays(Convert.ToDouble(diasPagoVoluntario));

                facturaIndividual.PaymentDetails = new FacturaElectronicaV321.InstallmentType[1];
                facturaIndividual.PaymentDetails[0] = detallesDelPago;
            }

            eFactura.Invoices[0] = facturaIndividual;

            decimal totalFacturado = 0;

            int totalFilasImpuesto = 0;
            bool insertado = false;

            // Impuestos (Agrupados por % de impuesto)
            eFactura.Invoices[0].TaxesOutputs = new FacturaElectronicaV321.TaxOutputType[factura.LineasFactura.Count];
            for (int i = 0; i < factura.LineasFactura.Count && respuesta.Resultado == ResultadoProceso.OK; i++)
            {
                totalFacturado += factura.LineasFactura[i].Total;
                insertado = false;

                for (int imp = 0; imp < totalFilasImpuesto; imp++)
                {
                    /*if (factura.LineasFactura[i] != null)
                    {

                        if (factura.LineasFactura[i] != null || eFactura.Invoices[0].TaxesOutputs[imp].TaxRate == factura.LineasFactura[i].PtjImpuesto.ToString("0.00").Replace(',', '.'))
                        {
                            eFactura.Invoices[0].TaxesOutputs[imp].TaxableBase.TotalAmount = (Convert.ToDouble(eFactura.Invoices[0].TaxesOutputs[imp].TaxableBase.TotalAmount.Replace(".", ",")) + Convert.ToDouble(factura.LineasFactura[i].CBase)).ToString("N2");
                            eFactura.Invoices[0].TaxesOutputs[imp].TaxAmount.TotalAmount = (Convert.ToDouble(eFactura.Invoices[0].TaxesOutputs[imp].TaxAmount.TotalAmount.Replace(".", ",")) + Convert.ToDouble(factura.LineasFactura[i].ImpImpuesto)).ToString("N2");

                            insertado = true;
                        }
                    }*/

                    if (eFactura.Invoices[0].TaxesOutputs[imp].TaxRate == Convert.ToDouble(factura.LineasFactura[i].PtjImpuesto.ToString("0.00")))
                    {
                        eFactura.Invoices[0].TaxesOutputs[imp].TaxableBase.TotalAmount = Convert.ToDouble((Convert.ToDouble(eFactura.Invoices[0].TaxesOutputs[imp].TaxableBase.TotalAmount.ToString("N2")) + Convert.ToDouble(factura.LineasFactura[i].CBase.ToString("N2"))).ToString("N2"));
                        eFactura.Invoices[0].TaxesOutputs[imp].TaxAmount.TotalAmount = Convert.ToDouble((Convert.ToDouble(eFactura.Invoices[0].TaxesOutputs[imp].TaxAmount.TotalAmount.ToString("N2")) + Convert.ToDouble(factura.LineasFactura[i].ImpImpuesto.ToString("N2"))).ToString("N2"));

                        insertado = true;
                    }
                }

                if (insertado == false)
                {
                    FacturaElectronicaV321.AmountType tAmountType = new FacturaElectronicaV321.AmountType();
                    FacturaElectronicaV321.AmountType bAmountType = new FacturaElectronicaV321.AmountType();
                    bAmountType.TotalAmount = Convert.ToDouble(factura.LineasFactura[i].CBase.ToString("N2"));
                    tAmountType.TotalAmount = Convert.ToDouble(factura.LineasFactura[i].ImpImpuesto.ToString("N2"));
                    FacturaElectronicaV321.TaxType oTax = new FacturaElectronicaV321.TaxType();
                    oTax.TaxableBase = bAmountType;
                    oTax.TaxAmount = bAmountType;
                    oTax.TaxTypeCode = FacturaElectronicaV321.TaxTypeCodeType.Item01;
                    oTax.TaxRate = Convert.ToDouble(factura.LineasFactura[i].PtjImpuesto);
                    FacturaElectronicaV321.TaxOutputType oTaxOutputType = new FacturaElectronicaV321.TaxOutputType()
                    {
                        TaxTypeCode = FacturaElectronicaV321.TaxTypeCodeType.Item01,
                        TaxRate = Convert.ToDouble(factura.LineasFactura[i].PtjImpuesto),
                        TaxableBase = bAmountType,
                        TaxAmount = tAmountType
                    };
                    totalFilasImpuesto++;

                    int countImp = 0;
                    for (countImp = 0; countImp < eFactura.Invoices[0].TaxesOutputs.Count(); countImp++)
                        if (eFactura.Invoices[0].TaxesOutputs[countImp] == null)
                            break;

                    eFactura.Invoices[0].TaxesOutputs[countImp] = oTaxOutputType;
                }
            }

            decimal totalImpuestos = 0;
            foreach (cLineaFacturaBO linea in factura.LineasFactura)
                totalImpuestos += linea.ImpImpuesto;

            // //Totales
            FacturaElectronicaV321.InvoiceTotalsType oInvoiceTotals = new FacturaElectronicaV321.InvoiceTotalsType();
            oInvoiceTotals.InvoiceTotal = Convert.ToDouble(totalFacturado.ToString("N2"));
            oInvoiceTotals.TotalGrossAmount = Convert.ToDouble((Convert.ToDouble(totalFacturado.ToString("N2")) - Convert.ToDouble(totalImpuestos.ToString("N2"))).ToString("N2"));
            oInvoiceTotals.TotalGrossAmountBeforeTaxes = Convert.ToDouble((Convert.ToDouble(totalFacturado.ToString("N2")) - Convert.ToDouble(totalImpuestos.ToString("N2"))).ToString("N2"));
            oInvoiceTotals.TotalTaxOutputs = Convert.ToDouble(totalImpuestos.ToString("N2"));
            oInvoiceTotals.TotalOutstandingAmount = Convert.ToDouble(totalFacturado.ToString("N2"));
            oInvoiceTotals.TotalExecutableAmount = Convert.ToDouble(totalFacturado.ToString("N2"));
            oInvoiceTotals.TotalTaxesWithheld = 0.00;
            eFactura.Invoices[0].InvoiceTotals = oInvoiceTotals;

            // Lineas de factura
            int fila = 0; // Número de fila en el vector
            int numeroDeLineas = factura.LineasFactura.Count;
            eFactura.Invoices[0].Items = new FacturaElectronicaV321.InvoiceLineType[factura.LineasFactura.Count * 9]; // Número máximo de escalados
            for (int i = 0; i < numeroDeLineas && respuesta.Resultado == ResultadoProceso.OK; i++)
            {
                cTarifaBO tarifa = new cTarifaBO();
                tarifa.Codigo = factura.LineasFactura[i].CodigoTarifa;
                tarifa.CodigoServicio = factura.LineasFactura[i].CodigoServicio;
                cTarifaBL.Obtener(ref tarifa, out respuesta);
                if (respuesta.Resultado != ResultadoProceso.OK)
                    return respuesta;
                cTarifaBL.ObtenerServicio(ref tarifa, out respuesta);
                if (respuesta.Resultado != ResultadoProceso.OK)
                    return respuesta;

                FacturaElectronicaV321.InvoiceLineType oInvoiceLine;

                if (factura.LineasFactura[i].Precio != 0 && factura.LineasFactura[i].Unidades != 0)
                {
                    oInvoiceLine = new FacturaElectronicaV321.InvoiceLineType()
                    {
                        IssuerContractReference = factura.ContratoCodigo.ToString(),
                        IssuerTransactionDate = factura.Fecha.Value,
                        ReceiverContractReference = factura.ContratoCodigo.ToString(),
                        ArticleCode = tarifa.CodigoServicio.ToString() + "-" + tarifa.Codigo.ToString() + " - Cuota",
                        ItemDescription = tarifa.Servicio.Descripcion + " - " + tarifa.Descripcion,
                        Quantity = Convert.ToDouble(factura.LineasFactura[i].Unidades),
                        UnitOfMeasure = FacturaElectronicaV321.UnitOfMeasureType.Item01,
                        UnitOfMeasureSpecified = true,
                        UnitPriceWithoutTax = Convert.ToDouble(factura.LineasFactura[i].Precio),
                        TotalCost = Convert.ToDouble(factura.LineasFactura[i].ImporteCuota.ToString("N2")), //17-03-2016
                        GrossAmount = Convert.ToDouble(factura.LineasFactura[i].ImporteCuota.ToString("N2"))//17-03-2016
                    };

                    oInvoiceLine.TaxesOutputs = new FacturaElectronicaV321.InvoiceLineTypeTax[1];
                    oInvoiceLine.TaxesOutputs[0] = new FacturaElectronicaV321.InvoiceLineTypeTax();
                    oInvoiceLine.TaxesOutputs[0].TaxRate = Convert.ToDouble(factura.LineasFactura[i].PtjImpuesto);
                    oInvoiceLine.TaxesOutputs[0].TaxableBase = new FacturaElectronicaV321.AmountType();
                    oInvoiceLine.TaxesOutputs[0].TaxableBase.TotalAmount = Convert.ToDouble((factura.LineasFactura[i].Precio * factura.LineasFactura[i].Unidades).ToString("N2"));//17-03-2016

                    eFactura.Invoices[0].Items[fila] = oInvoiceLine;
                }

                if (tarifa.Servicio.Tipo == "M") // Solo para servicios medidos
                {
                    for (int c = 0; c < 9; c++)  // Insertar tantas filas como escalados utilizados tenga la línea de factura
                    {
                        if (factura.LineasFactura[i].ArrayUnidades[c] != 0 && factura.LineasFactura[i].ArrayPrecios[c] != 0)
                        {
                            // Líneas de escalados
                            eFactura.Invoices[0].Items[fila + 1] = CreateInvoiceLineForServiceMeasured(factura, tarifa, factura.LineasFactura[i], c);
                            fila++;
                        }
                    }
                }
                fila++;
            }

            cOficinaContableBO ofiCon = cOficinasContablesBL.Obtener(contrato.FacturaeOficinaContable, out respuesta);
            bool oficinasConExtensiones = respuesta.Resultado == ResultadoProceso.OK && ofiCon.EnviarConExtensiones.HasValue && ofiCon.EnviarConExtensiones.Value;

            if (factura.PeriodoCodigo.Substring(0, 1) != "0")
            {
                // Extensión de la factura electrónica
                FacturaElectronicaV321.UtilitiesExtension UtilitiesExtension = new FacturaElectronicaV321.UtilitiesExtension()
                {
                    Version = "1.0",
                };

                cContadorBO contadorInstalado = cCtrConBL.ObtenerUltimoContadorInstalado(factura.ContratoCodigo.Value, out respuesta);
                if (respuesta.Resultado == ResultadoProceso.Error)
                    return respuesta;
                respuesta.Resultado = ResultadoProceso.OK;

                // Datos de suministro
                FacturaElectronicaV321.DatosDelSuministroType datosSuministro = new FacturaElectronicaV321.DatosDelSuministroType();
                datosSuministro.CUPS = contadorInstalado != null ? contadorInstalado.NumSerie : String.Empty;
                datosSuministro.Contrato = new FacturaElectronicaV321.ContratoType();
                datosSuministro.Contrato.RefContratoEmpresa = factura.ContratoCodigo.ToString();
                datosSuministro.Contrato.ReferenciaPropiaCliente = contrato.TitularCodigo.ToString();
                datosSuministro.Distribuidora = sociedad.Nombre;

                cContratoBL.ObtenerInmueble(ref contrato, out respuesta);
                if (respuesta.Resultado != ResultadoProceso.OK)
                    return respuesta;

                inmueble = contrato.InmuebleBO;
                respuesta = cInmuebleBL.ObtenerPoblacion(ref inmueble);
                if (respuesta.Resultado != ResultadoProceso.OK)
                    return respuesta;

                respuesta = cInmuebleBL.ObtenerProvincia(ref inmueble);
                if (respuesta.Resultado != ResultadoProceso.OK)
                    return respuesta;

                datosSuministro.DireccionSuministro = new FacturaElectronicaV321.DireccionSuministroType();
                datosSuministro.DireccionSuministro.Direccion = contrato.InmuebleBO.Direccion;
                datosSuministro.DireccionSuministro.CodigoPostal = contrato.InmuebleBO.CodigoPostal;
                datosSuministro.DireccionSuministro.Poblacion = inmueble.Poblacion.Descripcion;
                datosSuministro.DireccionSuministro.Provincia = inmueble.Provincia.Descripcion;
                datosSuministro.DireccionSuministro.Pais = "ESP";
                datosSuministro.DireccionSuministro.RefCatastral = contrato.InmuebleBO.RefCatastral;

                eFactura.Invoices[0].AdditionalData = new FacturaElectronicaV321.AdditionalDataType();
                eFactura.Invoices[0].AdditionalData.InvoiceAdditionalInformation =
                " Dirección de suministro: " + contrato.InmuebleBO.Direccion + "\n" +
                contrato.InmuebleBO.CodigoPostal + " - " + inmueble.Poblacion.Descripcion + " - " + inmueble.Provincia.Descripcion + "\n";
                eFactura.Invoices[0].AdditionalData.InvoiceAdditionalInformation +=
                " Fecha y lectura anterior: " + (factura.FechaLecturaAnterior.HasValue ? factura.FechaLecturaAnterior.Value.ToShortDateString() + "-" : String.Empty) + factura.LecturaAnterior.ToString() + "m3\n" +
                " Fecha y lectura actual: " + (factura.FechaLecturaFactura.HasValue ? factura.FechaLecturaFactura.Value.ToShortDateString() + "-" : String.Empty) + factura.LecturaFactura.ToString() + "m3\n" +
                " Consumo: " + (factura.ConsumoFactura.HasValue ? factura.ConsumoFactura.Value : 0).ToString() + "m3\n";
                eFactura.Invoices[0].AdditionalData.InvoiceAdditionalInformation += " Total: " + (factura.TotalFacturado.HasValue ? factura.TotalFacturado.Value.ToString("N2") : "0").ToString();
                eFactura.Invoices[0].AdditionalData.InvoiceAdditionalInformation += incidencia.MCalculo == "E" || incidencia.MCalculo == "EP" ? "Estimada\n" : String.Empty;

                UtilitiesExtension.DatosDelSuministro = datosSuministro;

                if (oficinasConExtensiones)
                {
                    short aguaCodigo;
                    respuesta = cParametroBL.GetShort("SERVICIO_AGUA", out aguaCodigo);
                    if (respuesta.Resultado != ResultadoProceso.OK)
                        return respuesta;

                    cLineaFacturaBO lineaAgua = new cLineaFacturaBO();
                    lineaAgua.CodigoServicio = aguaCodigo;
                    lineaAgua.Contrato = factura.ContratoCodigo.HasValue ? factura.ContratoCodigo.Value : 0;
                    lineaAgua.Periodo = factura.PeriodoCodigo;
                    lineaAgua.Version = factura.Version.HasValue ? factura.Version.Value : (short)0;
                    lineaAgua.FacturaCodigo = factura.FacturaCodigo.HasValue ? factura.FacturaCodigo.Value : (short)0;

                    new cLineasFacturaBL().Obtener(ref lineaAgua, out respuesta);

                    if (respuesta.Resultado != ResultadoProceso.OK)
                    {
                        if (respuesta.Resultado == ResultadoProceso.Error)
                            return respuesta;
                        else
                            respuesta.Resultado = ResultadoProceso.OK;
                    }
                    else
                    {
                        cTarvalBO tarVal = new cTarvalBO();
                        tarVal.Codigo = lineaAgua.CodigoTarifa;
                        tarVal.CodigoServicio = lineaAgua.CodigoServicio;
                        cTarvalBL.Obtener(ref tarVal, out respuesta);

                        if (respuesta.Resultado == ResultadoProceso.Error)
                            return respuesta;
                        respuesta.Resultado = ResultadoProceso.OK;
                        if (respuesta.Resultado == ResultadoProceso.OK)
                        {
                            datosSuministro.ReferenciaLegal = new FacturaElectronicaV321.ReferenciaLegalType();
                            datosSuministro.ReferenciaLegal.BOEBOCA = tarVal.LegalAvb;
                        }

                        if (contrato != null && contrato.UsoCodigo.HasValue)
                        {
                            datosSuministro.Usos = new FacturaElectronicaV321.UsosType();
                            if (contrato.UsoCodigo == 1) // Doméstico = 1
                                datosSuministro.Usos.NumeroViviendas = Convert.ToInt32(lineaAgua.Unidades).ToString();
                            else
                                datosSuministro.Usos.NumeroLocales = Convert.ToInt32(lineaAgua.Unidades).ToString();
                        }
                    }

                    // Si la factura es domiciliada
                    if (!String.IsNullOrEmpty(contrato.Iban) && !String.IsNullOrEmpty(contrato.Bic))
                    {
                        cBicBO bic = cBicBL.Obtener(contrato.Bic, out respuesta);
                        if (respuesta.Resultado != ResultadoProceso.OK)
                            return respuesta;
                        datosSuministro.NombreBanco = bic.Nombre;
                        datosSuministro.TitularBancario = !String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularNombre : contrato.PagadorNombre;
                    }

                    datosSuministro.OrigenFactura = "ES";
                    datosSuministro.IDDocumento = codigoExplotacion + "-" + factura.SerieCodigo.ToString() + factura.Numero.ToString();
                    datosSuministro.TotalAPagar = Convert.ToDouble(factura.TotalFacturado.HasValue ? factura.TotalFacturado.Value : 0);

                    // Medidas
                    FacturaElectronicaV321.UtilitiesMedidaType medidas = new FacturaElectronicaV321.UtilitiesMedidaType();
                    medidas.MedidasSobreEquipo = new FacturaElectronicaV321.MedidaSobreEquipoType[1];
                    medidas.MedidasSobreEquipo[0] = new FacturaElectronicaV321.MedidaSobreEquipoType();
                    medidas.MedidasSobreEquipo[0].Calibre = contadorInstalado.Diametro.ToString();
                    medidas.MedidasSobreEquipo[0].LecturaDesdeSpecified = true;
                    medidas.MedidasSobreEquipo[0].LecturaDesde = factura.LecturaAnterior;
                    medidas.MedidasSobreEquipo[0].LecturaHastaSpecified = true;
                    medidas.MedidasSobreEquipo[0].LecturaHasta = factura.LecturaFactura;

                    incidencia.Codigo = String.IsNullOrEmpty(factura.InspectorIncidenciaLectura) ? (String.IsNullOrEmpty(factura.LectorIncidenciaLectura) ? null : factura.LectorIncidenciaLectura) : factura.InspectorIncidenciaLectura;

                    if (!String.IsNullOrEmpty(incidencia.Codigo))
                    {
                        new cIncilecBL().Obtener(ref incidencia, out respuesta);
                        if (respuesta.Resultado != ResultadoProceso.OK)
                            return respuesta;
                    }

                    medidas.MedidasSobreEquipo[0].TipoDeLecturaActual = incidencia != null && (incidencia.MCalculo == "E" || incidencia.MCalculo == "EP") ? "Estimado" : "Leido";
                    if (medidas.MedidasSobreEquipo[0].TipoDeLecturaActual != "Estimada")
                    {
                        medidas.MedidasSobreEquipo[0].ConsumoLeidoSpecified = true;
                        medidas.MedidasSobreEquipo[0].ConsumoLeido = factura.ConsumoFactura.HasValue ? factura.ConsumoFactura.Value : 0;
                    }
                    else
                    {
                        medidas.MedidasSobreEquipo[0].ConsumoCalculadoSpecified = true;
                        medidas.MedidasSobreEquipo[0].ConsumoCalculado = factura.ConsumoFactura.HasValue ? factura.ConsumoFactura.Value : 0;
                    }

                    cFacturasBL.ObtenerPeriodo(ref factura, out respuesta);
                    if (respuesta.Resultado != ResultadoProceso.OK)
                        return respuesta;

                    // Histórico de consumos
                    FacturaElectronicaV321.HistoricoConsumoType[] historicos = new FacturaElectronicaV321.HistoricoConsumoType[2];
                    historicos[0] = new FacturaElectronicaV321.HistoricoConsumoType();
                    historicos[0].Periodo = factura.PeriodoCodigo;
                    historicos[0].Descripcion = factura.Periodo.Descripcion;
                    historicos[0].ValorSpecified = true;
                    historicos[0].Valor = factura.ConsumoFactura.HasValue ? factura.ConsumoFactura.Value : 0;
                    historicos[0].UnidadMedida = "m3";
                    historicos[0].FechaIniPeriodoSpecified = true;
                    historicos[0].FechaIniPeriodo = factura.FechaLecturaAnterior;
                    historicos[0].FechaFinPeriodoSpecified = true;
                    historicos[0].FechaFinPeriodo = factura.FechaLecturaFactura;
                    historicos[0].TipoCalculo = "Exacto";

                    cFacturaBO facturaAnterior = new cFacturaBO();
                    facturaAnterior.PeriodoCodigo = new cPeriodoBL().ObtenerPeriodoConsumoAnterior(factura.PeriodoCodigo, out respuesta);
                    facturaAnterior.ContratoCodigo = factura.ContratoCodigo;

                    if (respuesta.Resultado == ResultadoProceso.Error)
                        return respuesta;

                    if (respuesta.Resultado == ResultadoProceso.OK)
                    {
                        cFacturasBL.Obtener(ref facturaAnterior, out respuesta);

                        if (respuesta.Resultado != ResultadoProceso.OK)
                            return respuesta;

                        historicos[1] = new FacturaElectronicaV321.HistoricoConsumoType();
                        historicos[1].Periodo = facturaAnterior.PeriodoCodigo;
                        historicos[1].Descripcion = facturaAnterior.Periodo.Descripcion;
                        historicos[1].ValorSpecified = true;
                        historicos[1].Valor = facturaAnterior.ConsumoFactura.HasValue ? facturaAnterior.ConsumoFactura.Value : 0;
                        historicos[1].UnidadMedida = "m3";
                        historicos[1].FechaIniPeriodoSpecified = true;
                        historicos[1].FechaIniPeriodo = facturaAnterior.FechaLecturaAnterior;
                        historicos[1].FechaFinPeriodoSpecified = true;
                        historicos[1].FechaFinPeriodo = facturaAnterior.FechaLecturaFactura;
                        historicos[1].TipoCalculo = "Exacto";
                    }

                    respuesta.Resultado = ResultadoProceso.OK;

                    // Datos adicionales
                    string[] datosAdicionales = new string[1];
                    string ibanOculto = "INGRESO EN CUENTA DE " + nombreExplotacion;
                    if (!String.IsNullOrEmpty(contrato.Iban))
                    {
                        if (contrato.Iban.Length > 34 || contrato.Iban.Length < 24)
                            ibanOculto = "INGRESO EN CUENTA DE " + nombreExplotacion;
                        else
                            ibanOculto = "Será cargada en: " + contrato.Iban.Substring(0, 12) + "********" + contrato.Iban.Substring(20);
                    }

                    datosAdicionales[0] = ibanOculto + " ";

                    UtilitiesExtension.UtilitiesMedida = medidas;
                    UtilitiesExtension.UtilitiesHistoricoConsumos = historicos;
                    UtilitiesExtension.DatosPagoAdicionales = datosAdicionales;

                    XmlDocument xmld2 = new XmlDocument();

                    //XmlSerializerNamespaces namespaces = new XmlSerializerNamespaces();
                    //namespaces.Add("ex", "http://www.facturae.es/Facturae/Extensions/Utilities");

                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (XmlTextWriter xmltw = new XmlTextWriter(ms, Encoding.UTF8))
                        {
                            XmlSerializer xmlser = new XmlSerializer(typeof(FacturaElectronicaV321.UtilitiesExtension));
                            //xmlser.Serialize(xmltw, UtilitiesExtension, namespaces);
                            xmlser.Serialize(xmltw, UtilitiesExtension);
                            ms.Seek(0, SeekOrigin.Begin);
                            xmld2.Load(ms);
                        }
                    }
                    eFactura.Invoices[0].AdditionalData.Extensions = new FacturaElectronicaV321.ExtensionsType();
                    eFactura.Invoices[0].AdditionalData.Extensions.Any = new XmlElement[] { xmld2.DocumentElement };
                }
            }

            // Actualizar el campo referente al momento en el que se encuentra el proceso SERES
            if (respuesta.Resultado == ResultadoProceso.OK)
            {
                factura.EnvSERES = "E";
                string log = String.Empty;

                cFacturasBL.Actualizar(factura, false, out log, out respuesta);
            }

            if (respuesta.Resultado == ResultadoProceso.OK)
                facturaXML = Serializar(eFactura);

            return respuesta;
        }

        //****************************************************************
        //COMPLEJIDAD EXCESIVA DEL METODO casi 800 Lineas!!!!
        public static cRespuesta GenerarFacturaV32(cFacturaBO factura, out XmlDocument facturaXML)
        {
            cRespuesta respuesta = new cRespuesta();
            FacturaElectronicaV32.Facturae eFactura = new FacturaElectronicaV32.Facturae();
            facturaXML = new XmlDocument();

            cInmuebleBO inmueble = new cInmuebleBO();
            cIncilecBO incidencia = new cIncilecBO();

            short sociedadCodigo;
            cSociedadBO sociedad = new cSociedadBO();
            respuesta = cParametroBL.GetShort("SOCIEDAD_POR_DEFECTO", out sociedadCodigo);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            string bicElectronico;
            respuesta = cParametroBL.GetString("BIC_ELECTRONICO", out bicElectronico);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;
            string ibanElectronico;
            respuesta = cParametroBL.GetString("IBAN_ELECTRONICO", out ibanElectronico);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            string diasPagoVoluntario;
            respuesta = cParametroBL.GetString("DIAS_PAGO_VOLUNTARIO", out diasPagoVoluntario);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            sociedad.Codigo = sociedadCodigo;
            cSociedadBL.Obtener(ref sociedad, out respuesta);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            cFacturasBL.ObtenerLineas(ref factura, out respuesta);
            if (respuesta.Resultado == ResultadoProceso.OK)
                cFacturasBL.ObtenerImporteFacturado(ref factura, null);
            else
                return respuesta;

            string codigoExplotacion;
            respuesta = cParametroBL.GetString("EXPLOTACION_CODIGO", out codigoExplotacion);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;
            else
                codigoExplotacion = cAplicacion.FixedLengthString(Convert.ToInt32(codigoExplotacion).ToString(), 3, '0', false, false);

            decimal totalFacturadoTodas = factura.TotalFacturado.HasValue ? factura.TotalFacturado.Value : 0;

            eFactura.FileHeader = new FacturaElectronicaV32.FileHeaderType();

            eFactura.FileHeader.Batch = new FacturaElectronicaV32.BatchType();
            eFactura.FileHeader.Batch.InvoicesCount = 1;
            eFactura.FileHeader.Batch.BatchIdentifier = "ES" + sociedad.Nif + factura.Numero.ToString() + "-" + codigoExplotacion + "-" + factura.SerieCodigo.ToString();
            eFactura.FileHeader.Batch.TotalInvoicesAmount = new FacturaElectronicaV32.AmountType();
            eFactura.FileHeader.Batch.TotalInvoicesAmount.TotalAmount = totalFacturadoTodas.ToString("N2");
            eFactura.FileHeader.Batch.TotalOutstandingAmount = new FacturaElectronicaV32.AmountType();
            eFactura.FileHeader.Batch.TotalOutstandingAmount.TotalAmount = totalFacturadoTodas.ToString("N2");
            eFactura.FileHeader.Batch.TotalExecutableAmount = new FacturaElectronicaV32.AmountType();
            eFactura.FileHeader.Batch.TotalExecutableAmount.TotalAmount = totalFacturadoTodas.ToString("N2");
            eFactura.FileHeader.Batch.InvoiceCurrencyCode = new FacturaElectronicaV32.CurrencyCodeType();
            eFactura.FileHeader.Batch.InvoiceCurrencyCode = FacturaElectronicaV32.CurrencyCodeType.EUR;

            eFactura.Parties = new FacturaElectronicaV32.PartiesType();

            eFactura.Parties.SellerParty = new FacturaElectronicaV32.BusinessType();
            eFactura.Parties.SellerParty.TaxIdentification = new FacturaElectronicaV32.TaxIdentificationType();
            eFactura.Parties.SellerParty.TaxIdentification.PersonTypeCode = FacturaElectronicaV32.PersonTypeCodeType.J;
            eFactura.Parties.SellerParty.TaxIdentification.ResidenceTypeCode = FacturaElectronicaV32.ResidenceTypeCodeType.R;
            eFactura.Parties.SellerParty.TaxIdentification.TaxIdentificationNumber = "ES" + sociedad.Nif;

            string cb;
            respuesta = cParametroBL.GetString("CB_SERES", out cb);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            FacturaElectronicaV32.AddressType bAddressType = new FacturaElectronicaV32.AddressType();
            if (!String.IsNullOrEmpty(sociedad.Nacion))
                bAddressType.CountryCode = (FacturaElectronicaV32.CountryType)Enum.Parse(typeof(FacturaElectronicaV32.CountryType), sociedad.Nacion);
            bAddressType.Address = sociedad.Domicilio;
            bAddressType.PostCode = sociedad.CPostal;
            bAddressType.Province = sociedad.Provincia;
            bAddressType.Town = sociedad.Poblacion;

            string nombreExplotacion;
            respuesta = cParametroBL.GetString("EXPLOTACION", out nombreExplotacion);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            eFactura.Parties.SellerParty.AdministrativeCentres = new FacturaElectronicaV32.AdministrativeCentreType[1];
            eFactura.Parties.SellerParty.AdministrativeCentres[0] = new FacturaElectronicaV32.AdministrativeCentreType();
            eFactura.Parties.SellerParty.AdministrativeCentres[0].RoleTypeCodeSpecified = true;
            eFactura.Parties.SellerParty.AdministrativeCentres[0].RoleTypeCode = FacturaElectronicaV32.RoleTypeCodeType.Item09;
            eFactura.Parties.SellerParty.AdministrativeCentres[0].CentreCode = cb;
            eFactura.Parties.SellerParty.AdministrativeCentres[0].Name = nombreExplotacion;
            eFactura.Parties.SellerParty.AdministrativeCentres[0].Item = bAddressType;

            FacturaElectronicaV32.LegalEntityType bEntidadType = new FacturaElectronicaV32.LegalEntityType();
            bEntidadType.CorporateName = sociedad.Nombre;
            bEntidadType.TradeName = sociedad.Nombre;
            bEntidadType.ContactDetails = new FacturaElectronicaV32.ContactDetailsType();
            bEntidadType.ContactDetails.Telephone = sociedad.Telefono1;
            bEntidadType.ContactDetails.ElectronicMail = sociedad.Email;

            bEntidadType.Item = bAddressType;
            eFactura.Parties.SellerParty.Item = bEntidadType;

            eFactura.Invoices = new FacturaElectronicaV32.InvoiceType[1];

            cContratoBO contrato = new cContratoBO();
            contrato.Codigo = factura.ContratoCodigo.Value;
            contrato.Version = factura.ContratoVersion.Value;
            cContratoBL.Obtener(ref contrato, out respuesta);
            factura.Contrato = contrato;

            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            eFactura.Parties.BuyerParty = new FacturaElectronicaV32.BusinessType();

            // Obtenemos el ISO 2 del país del pagador
            cCatalogoPaisBO catalogoPais = cCatalogosPaisesBL.Obtener(String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularNacion : contrato.PagadorNacion, out respuesta);
            if (respuesta.Resultado != ResultadoProceso.OK)
                return respuesta;

            eFactura.Parties.BuyerParty.TaxIdentification = new FacturaElectronicaV32.TaxIdentificationType();
            eFactura.Parties.BuyerParty.TaxIdentification.PersonTypeCode = cVarios.ValidateCIF(String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularDocIden : contrato.PagadorDocIden) ? FacturaElectronicaV32.PersonTypeCodeType.J : FacturaElectronicaV32.PersonTypeCodeType.F;
            eFactura.Parties.BuyerParty.TaxIdentification.TaxIdentificationNumber = catalogoPais.ISOAlfa2 + (String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularDocIden : contrato.PagadorDocIden);
            eFactura.Parties.BuyerParty.TaxIdentification.ResidenceTypeCode = FacturaElectronicaV32.ResidenceTypeCodeType.R;

            // Dirección de la persona jurídica/fisica de la factura
            FacturaElectronicaV32.AddressType oAddressType = new FacturaElectronicaV32.AddressType();
            if (!String.IsNullOrEmpty(String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularNacion : contrato.PagadorNacion))
                oAddressType.CountryCode = (FacturaElectronicaV32.CountryType)Enum.Parse(typeof(FacturaElectronicaV32.CountryType), String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularNacion : contrato.PagadorNacion);
            oAddressType.Address = String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularDireccion : contrato.PagadorDireccion;
            oAddressType.PostCode = String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularCpostal : contrato.PagadorCpostal;
            oAddressType.Province = String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularProvincia : contrato.PagadorProvincia;
            oAddressType.Town = String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularPoblacion : contrato.PagadorPoblacion;

            eFactura.Parties.BuyerParty.AdministrativeCentres = new FacturaElectronicaV32.AdministrativeCentreType[4];
            eFactura.Parties.BuyerParty.AdministrativeCentres[0] = new FacturaElectronicaV32.AdministrativeCentreType();
            eFactura.Parties.BuyerParty.AdministrativeCentres[0].CentreCode = contrato.FacturaeOficinaContable;
            eFactura.Parties.BuyerParty.AdministrativeCentres[0].RoleTypeCodeSpecified = true;
            eFactura.Parties.BuyerParty.AdministrativeCentres[0].RoleTypeCode = FacturaElectronicaV32.RoleTypeCodeType.Item01;
            eFactura.Parties.BuyerParty.AdministrativeCentres[0].Item = oAddressType;
            eFactura.Parties.BuyerParty.AdministrativeCentres[0].CentreDescription = "Oficina contable";

            eFactura.Parties.BuyerParty.AdministrativeCentres[1] = new FacturaElectronicaV32.AdministrativeCentreType();
            eFactura.Parties.BuyerParty.AdministrativeCentres[1].CentreCode = contrato.FacturaeOrganismoGestor;
            eFactura.Parties.BuyerParty.AdministrativeCentres[1].RoleTypeCodeSpecified = true;
            eFactura.Parties.BuyerParty.AdministrativeCentres[1].RoleTypeCode = FacturaElectronicaV32.RoleTypeCodeType.Item02;
            eFactura.Parties.BuyerParty.AdministrativeCentres[1].Item = oAddressType;
            eFactura.Parties.BuyerParty.AdministrativeCentres[1].CentreDescription = "Órgano Gestor";

            eFactura.Parties.BuyerParty.AdministrativeCentres[2] = new FacturaElectronicaV32.AdministrativeCentreType();
            eFactura.Parties.BuyerParty.AdministrativeCentres[2].CentreCode = contrato.FacturaeUnidadTramitadora;
            eFactura.Parties.BuyerParty.AdministrativeCentres[2].RoleTypeCodeSpecified = true;
            eFactura.Parties.BuyerParty.AdministrativeCentres[2].RoleTypeCode = FacturaElectronicaV32.RoleTypeCodeType.Item03;
            eFactura.Parties.BuyerParty.AdministrativeCentres[2].Item = oAddressType;
            eFactura.Parties.BuyerParty.AdministrativeCentres[2].CentreDescription = "Unidad Tramitadora";

            eFactura.Parties.BuyerParty.AdministrativeCentres[3] = new FacturaElectronicaV32.AdministrativeCentreType();
            if (!String.IsNullOrEmpty( contrato.FacturaeOrganoProponente))
                eFactura.Parties.BuyerParty.AdministrativeCentres[3].CentreCode = contrato.FacturaeOrganoProponente;
            eFactura.Parties.BuyerParty.AdministrativeCentres[3].RoleTypeCodeSpecified = true;
            eFactura.Parties.BuyerParty.AdministrativeCentres[3].RoleTypeCode = FacturaElectronicaV32.RoleTypeCodeType.Item04;
            eFactura.Parties.BuyerParty.AdministrativeCentres[3].Item = oAddressType;
            eFactura.Parties.BuyerParty.AdministrativeCentres[3].CentreDescription = "Subdirección de compras";

            if(cVarios.ValidateCIF(String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularDocIden : contrato.PagadorDocIden))
            {
                FacturaElectronicaV32.LegalEntityType personaJuridica = new FacturaElectronicaV32.LegalEntityType();

                personaJuridica.CorporateName = String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularNombre : contrato.PagadorNombre;
                personaJuridica.ContactDetails = new FacturaElectronicaV32.ContactDetailsType();
                personaJuridica.ContactDetails.Telephone = contrato.Telefono1;
                personaJuridica.ContactDetails.ElectronicMail = contrato.Email;

                personaJuridica.Item = oAddressType;
                eFactura.Parties.BuyerParty.Item = personaJuridica;
            }
            else
            {
                FacturaElectronicaV32.IndividualType personaFisica = new FacturaElectronicaV32.IndividualType();
                personaFisica.Name = String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularNombre : contrato.PagadorNombre;
                personaFisica.FirstSurname = String.Empty;
                personaFisica.ContactDetails = new FacturaElectronicaV32.ContactDetailsType();
                personaFisica.ContactDetails.Telephone = contrato.Telefono1;
                personaFisica.ContactDetails.ElectronicMail = contrato.Email;

                personaFisica.Item = oAddressType;
                eFactura.Parties.BuyerParty.Item = personaFisica;
            }

            FacturaElectronicaV32.PlaceOfIssueType oPlaceOfIssueType = new FacturaElectronicaV32.PlaceOfIssueType();// Lugar donde se emite la factura
            oPlaceOfIssueType.PlaceOfIssueDescription = sociedad.Poblacion;
            oPlaceOfIssueType.PostCode = sociedad.CPostal;

            cPerzonaBO perzona = new cPerzonaBO();
            if(factura.PeriodoCodigo.Substring(0,1) != "0")
            {
                perzona.CodigoPeriodo = factura.PeriodoCodigo;
                perzona.CodigoZona = factura.ZonaCodigo;
                new cPerzonaBL().Obtener(ref perzona, out respuesta);
                if (respuesta.Resultado != ResultadoProceso.OK || !perzona.FPeriodoDesde.HasValue || !perzona.FPeriodoHasta.HasValue)
                    return respuesta;
            }

            // Periodo de facturación
            FacturaElectronicaV32.PeriodDates oPeriodDates = new FacturaElectronicaV32.PeriodDates();
            oPeriodDates.StartDate = factura.PeriodoCodigo.Substring(0,1) != "0" ? perzona.FPeriodoDesde.Value : new DateTime(factura.Fecha.Value.Year, factura.Fecha.Value.Month, 1);
            oPeriodDates.EndDate = factura.PeriodoCodigo.Substring(0, 1) != "0" ? perzona.FPeriodoHasta.Value : new DateTime(factura.Fecha.Value.Year, factura.Fecha.Value.Month, DateTime.DaysInMonth(factura.Fecha.Value.Year, factura.Fecha.Value.Month));

            //((XmlEnumAttribute)typeof(ReasonCodeType) factura.RazRectcod

            FacturaElectronicaV32.CorrectiveType rectificativa = null;
            if(factura.Version > 1)
            {

                respuesta = cFacturasBL.ObtenerRectificada(ref factura);
                if (respuesta.Resultado != ResultadoProceso.OK)
                    return respuesta;

                var razonCodigo = GetEnumValue<FacturaElectronicaV32.ReasonCodeType>(factura.RazRectcod);
                var razonDescripcion = GetEnumValue<FacturaElectronicaV32.ReasonDescriptionType>(factura.RazRectDescType);
                var metodoCorreccion = GetEnumValue<FacturaElectronicaV32.CorrectionMethodType>(factura.MeRect);
                var metodoDescripcion = GetEnumValue<FacturaElectronicaV32.CorrectionMethodDescriptionType>(factura.MeRectType);

                rectificativa = new FacturaElectronicaV32.CorrectiveType()
                {
                    InvoiceNumber = !String.IsNullOrEmpty(factura.FacturaRectificada.Numero) ? factura.FacturaRectificada.Numero.ToString() : String.Empty,
                    InvoiceSeriesCode = factura.FacturaRectificada.SerieCodigo.HasValue ? codigoExplotacion + "-" + factura.FacturaRectificada.SerieCodigo.ToString() : String.Empty,

                    ReasonCode = razonCodigo,
                    ReasonDescription = razonDescripcion,

                    TaxPeriod = new FacturaElectronicaV32.PeriodDates()
                    {
                        StartDate = factura.PeriodoCodigo.Substring(0,1) != "0" ? perzona.FPeriodoDesde.Value : new DateTime(factura.Fecha.Value.Year, factura.Fecha.Value.Month, 1),
                        EndDate = factura.PeriodoCodigo.Substring(0, 1) != "0" ? perzona.FPeriodoHasta.Value : new DateTime(factura.Fecha.Value.Year, factura.Fecha.Value.Month, DateTime.DaysInMonth(factura.Fecha.Value.Year, factura.Fecha.Value.Month)),
                    },

                    CorrectionMethod = metodoCorreccion,
                    CorrectionMethodDescription = metodoDescripcion,
                };
            }

            FacturaElectronicaV32.InvoiceType facturaIndividual =
                new FacturaElectronicaV32.InvoiceType()
                {
                    InvoiceHeader = new FacturaElectronicaV32.InvoiceHeaderType()
                    {
                        InvoiceNumber = factura.Numero.ToString(),
                        InvoiceSeriesCode = codigoExplotacion + "-" + factura.SerieCodigo.ToString(),
                        InvoiceDocumentType = FacturaElectronicaV32.InvoiceDocumentTypeType.FC,
                        InvoiceClass = factura.Version == 1 ? FacturaElectronicaV32.InvoiceClassType.OO : FacturaElectronicaV32.InvoiceClassType.OR, // Original o Rectificativa
                        Corrective = rectificativa,
                    },

                    InvoiceIssueData = new FacturaElectronicaV32.InvoiceIssueDataType()
                    {
                        IssueDate = factura.Fecha.Value,
                        PlaceOfIssue = oPlaceOfIssueType,
                        InvoicingPeriod = oPeriodDates,
                        InvoiceCurrencyCode = FacturaElectronicaV32.CurrencyCodeType.EUR,
                        TaxCurrencyCode = FacturaElectronicaV32.CurrencyCodeType.EUR,
                        LanguageName = FacturaElectronicaV32.LanguageCodeType.es,
                    }
                };

            if (/*contrato.FacturaePortal == cContratoBO.EFacturaePortal.JuntaCastillaMancha &&*/ String.IsNullOrEmpty(contrato.Iban))
            {

                FacturaElectronicaV32.InstallmentType detallesDelPago = new FacturaElectronicaV32.InstallmentType()
                {
                    PaymentMeans = FacturaElectronicaV32.PaymentMeansType.Item04,
                  
                    AccountToBeCredited = new FacturaElectronicaV32.AccountType( )
                    {
                     
                        ItemElementName = FacturaElectronicaV32.ItemChoiceType.IBAN,
                        Item = ibanElectronico,
                        BIC = bicElectronico
                     }
                };

                detallesDelPago.InstallmentAmount = totalFacturadoTodas.ToString();
                detallesDelPago.InstallmentDueDate = factura.Fecha.Value.AddDays(Convert.ToDouble(diasPagoVoluntario));

                facturaIndividual.PaymentDetails = new FacturaElectronicaV32.InstallmentType[1];
                facturaIndividual.PaymentDetails[0] = detallesDelPago;
            }

            eFactura.Invoices[0] = facturaIndividual;

            decimal totalFacturado = 0;

            int totalFilasImpuesto = 0;
            bool insertado = false;

            // Impuestos (Agrupados por % de impuesto)
            eFactura.Invoices[0].TaxesOutputs = new FacturaElectronicaV32.TaxOutputType[factura.LineasFactura.Count];
            for (int i = 0; i < factura.LineasFactura.Count && respuesta.Resultado == ResultadoProceso.OK; i++)
            {
                totalFacturado += factura.LineasFactura[i].Total;
                insertado = false;

                for (int imp = 0; imp < totalFilasImpuesto; imp++)  
                {
                    /*if (factura.LineasFactura[i] != null)
                    {

                        if (factura.LineasFactura[i] != null || eFactura.Invoices[0].TaxesOutputs[imp].TaxRate == factura.LineasFactura[i].PtjImpuesto.ToString("0.00").Replace(',', '.'))
                        {
                            eFactura.Invoices[0].TaxesOutputs[imp].TaxableBase.TotalAmount = (Convert.ToDouble(eFactura.Invoices[0].TaxesOutputs[imp].TaxableBase.TotalAmount.Replace(".", ",")) + Convert.ToDouble(factura.LineasFactura[i].CBase)).ToString("N2");
                            eFactura.Invoices[0].TaxesOutputs[imp].TaxAmount.TotalAmount = (Convert.ToDouble(eFactura.Invoices[0].TaxesOutputs[imp].TaxAmount.TotalAmount.Replace(".", ",")) + Convert.ToDouble(factura.LineasFactura[i].ImpImpuesto)).ToString("N2");

                            insertado = true;
                        }
                    }*/


                    if (eFactura.Invoices[0].TaxesOutputs[imp].TaxRate == factura.LineasFactura[i].PtjImpuesto.ToString("0.00").Replace(',', '.'))
                    {
                        eFactura.Invoices[0].TaxesOutputs[imp].TaxableBase.TotalAmount = (Convert.ToDouble(eFactura.Invoices[0].TaxesOutputs[imp].TaxableBase.TotalAmount) + Convert.ToDouble(factura.LineasFactura[i].CBase)).ToString("N2");
                        eFactura.Invoices[0].TaxesOutputs[imp].TaxAmount.TotalAmount = (Convert.ToDouble(eFactura.Invoices[0].TaxesOutputs[imp].TaxAmount.TotalAmount) + Convert.ToDouble(factura.LineasFactura[i].ImpImpuesto)).ToString("N2");

                        insertado = true;
                    }



                }
                        
                if(insertado == false)
                {
                    FacturaElectronicaV32.AmountType tAmountType = new FacturaElectronicaV32.AmountType();
                    FacturaElectronicaV32.AmountType bAmountType = new FacturaElectronicaV32.AmountType();
                    bAmountType.TotalAmount =Convert.ToDouble(factura.LineasFactura[i].CBase.ToString()).ToString("N2");
                    tAmountType.TotalAmount = Convert.ToDouble(factura.LineasFactura[i].ImpImpuesto.ToString()).ToString("N2");
                    FacturaElectronicaV32.TaxType oTax = new FacturaElectronicaV32.TaxType();
                    oTax.TaxableBase = bAmountType;
                    oTax.TaxAmount = bAmountType;
                    oTax.TaxTypeCode = FacturaElectronicaV32.TaxTypeCodeType.Item01;
                    oTax.TaxRate = factura.LineasFactura[i].PtjImpuesto.ToString();
                    FacturaElectronicaV32.TaxOutputType oTaxOutputType = new FacturaElectronicaV32.TaxOutputType()
                    {
                        TaxTypeCode = FacturaElectronicaV32.TaxTypeCodeType.Item01,
                        TaxRate = factura.LineasFactura[i].PtjImpuesto.ToString(),
                        TaxableBase = bAmountType,
                        TaxAmount = tAmountType
                    };
                    totalFilasImpuesto++;

                    int countImp = 0;
                    for (countImp = 0; countImp < eFactura.Invoices[0].TaxesOutputs.Count(); countImp++)
                        if (eFactura.Invoices[0].TaxesOutputs[countImp] == null)
                            break;

                    eFactura.Invoices[0].TaxesOutputs[countImp] = oTaxOutputType;
                }
            }

            decimal totalImpuestos = 0;
            foreach (cLineaFacturaBO linea in factura.LineasFactura)
                totalImpuestos += linea.ImpImpuesto;

            // Totales
            FacturaElectronicaV32.InvoiceTotalsType oInvoiceTotals = new FacturaElectronicaV32.InvoiceTotalsType();
            oInvoiceTotals.InvoiceTotal = totalFacturado.ToString();
            oInvoiceTotals.TotalGrossAmount = (totalFacturado - totalImpuestos).ToString();
            oInvoiceTotals.TotalGrossAmountBeforeTaxes = (totalFacturado - totalImpuestos).ToString();
            oInvoiceTotals.TotalTaxOutputs = totalImpuestos.ToString();
            oInvoiceTotals.TotalOutstandingAmount = totalFacturado.ToString();
            oInvoiceTotals.TotalExecutableAmount = totalFacturado.ToString();
            oInvoiceTotals.TotalTaxesWithheld = "0";
            eFactura.Invoices[0].InvoiceTotals = oInvoiceTotals;

            // Lineas de factura
            int fila = 0; // Número de fila en el vector
            int numeroDeLineas = factura.LineasFactura.Count;
            eFactura.Invoices[0].Items = new FacturaElectronicaV32.InvoiceLineType[factura.LineasFactura.Count * 9]; // Número máximo de escalados
            for (int i = 0; i < numeroDeLineas && respuesta.Resultado == ResultadoProceso.OK; i++)
            {
                cTarifaBO tarifa = new cTarifaBO();
                tarifa.Codigo = factura.LineasFactura[i].CodigoTarifa;
                tarifa.CodigoServicio = factura.LineasFactura[i].CodigoServicio;
                cTarifaBL.Obtener(ref tarifa, out respuesta);
                if (respuesta.Resultado != ResultadoProceso.OK)
                    return respuesta;
                cTarifaBL.ObtenerServicio(ref tarifa, out respuesta);
                if (respuesta.Resultado != ResultadoProceso.OK)
                    return respuesta;

                FacturaElectronicaV32.InvoiceLineType oInvoiceLine;
                // 
                if ((factura.LineasFactura[i].Precio != 0 && factura.LineasFactura[i].Unidades != 0)
                    // los iguiente para que no meta como cuota el minimo de soira
                    && (
                    (nombreExplotacion != "Soria") && ((factura.Contrato.Codigo == 31691 || factura.Contrato.Codigo == 31692))
                    && (factura.LineasFactura[i].CodigoServicio !=1 || factura.LineasFactura[i].CodigoServicio != 2)
                    )
                    
                    )
                {
                    oInvoiceLine = new FacturaElectronicaV32.InvoiceLineType()
                    {
                        IssuerContractReference = factura.ContratoCodigo.ToString(),
                        IssuerTransactionDate = factura.Fecha.Value,
                        ReceiverContractReference = factura.ContratoCodigo.ToString(),
                        ArticleCode = tarifa.CodigoServicio.ToString() + "-" + tarifa.Codigo.ToString() + " - Cuota",
                        ItemDescription = tarifa.Servicio.Descripcion + " - " + tarifa.Descripcion,
                        Quantity = Convert.ToDouble(factura.LineasFactura[i].Unidades),
                        UnitOfMeasure = FacturaElectronicaV32.UnitOfMeasureType.Item01,
                        UnitOfMeasureSpecified = true,
                        UnitPriceWithoutTax = factura.LineasFactura[i].Precio.ToString(),
                        TotalCost =Convert.ToDouble(factura.LineasFactura[i].ImporteCuota).ToString("N2"), //17-03-2016
                        GrossAmount = Convert.ToDouble(factura.LineasFactura[i].ImporteCuota).ToString("N2")//17-03-2016
                    };

                    oInvoiceLine.TaxesOutputs = new FacturaElectronicaV32.InvoiceLineTypeTax[1];
                    oInvoiceLine.TaxesOutputs[0] = new FacturaElectronicaV32.InvoiceLineTypeTax();
                    oInvoiceLine.TaxesOutputs[0].TaxRate = factura.LineasFactura[i].PtjImpuesto.ToString();
                    oInvoiceLine.TaxesOutputs[0].TaxableBase = new FacturaElectronicaV32.AmountType();
                    oInvoiceLine.TaxesOutputs[0].TaxableBase.TotalAmount = Convert.ToDouble(factura.LineasFactura[i].Precio * factura.LineasFactura[i].Unidades).ToString("N2");//17-03-2016

                    eFactura.Invoices[0].Items[fila] = oInvoiceLine;
                }

                double dQuantity;
                string sUnitPriceWithoutTax;
                bool insertoMinimoSoria = false;
                if ((tarifa.Servicio.Tipo == "M") || // Solo para servicios medidos O SORIA INSERSO
                     (
                    (nombreExplotacion == "Soria") && ((factura.Contrato.Codigo == 31691 || factura.Contrato.Codigo == 31692))
                    ) && (factura.LineasFactura[i].CodigoServicio == 1 || factura.LineasFactura[i].CodigoServicio == 2)
                    
                    )
                {
                 
                    for (int c = 0; c < 9; c++)  // Insertar tantas filas como escalados utilizados tenga la línea de factura
                    {
                     // si es Soria cuota y soria y no inserté ya
                            if (
                        (nombreExplotacion == "Soria") && ((factura.Contrato.Codigo == 31691 || factura.Contrato.Codigo == 31692))
                        &&  (factura.LineasFactura[i].CodigoServicio == 1 || factura.LineasFactura[i].CodigoServicio == 2)
                        && (insertoMinimoSoria == false) && factura.LineasFactura[i].Precio != 0
                        )
                            {
                                 dQuantity = Convert.ToDouble(factura.LineasFactura[i].ArrayEscalas[0]);
                            //round(Fields!fclPrecio.Value / (Fields!fclEscala1.Value / Fields!fclUnidades.Value), 6, System.MidpointRounding.AwayFromZero),""))
                            sUnitPriceWithoutTax = (factura.LineasFactura[i].Precio / factura.LineasFactura[i].ArrayEscalas[0] / factura.LineasFactura[i].Unidades).ToString();

                            // Líneas de Minimos Soria inserso

                            oInvoiceLine = new FacturaElectronicaV32.InvoiceLineType()
                                {
                                    IssuerContractReference = factura.ContratoCodigo.ToString(),
                                    IssuerTransactionDate = factura.Fecha.Value,
                                    ReceiverContractReference = factura.ContratoCodigo.ToString(),
                                    ArticleCode = tarifa.CodigoServicio.ToString() + "-" + tarifa.Codigo.ToString() + " - Consumo",
                                    ItemDescription = "Consumo hasta " + factura.LineasFactura[i].ArrayEscalas[c].ToString(),
                                    Quantity = dQuantity,
                                    UnitOfMeasure = FacturaElectronicaV32.UnitOfMeasureType.Item33,
                                    UnitOfMeasureSpecified = true,
                                    UnitPriceWithoutTax = factura.LineasFactura[i].ArrayPrecios[c].ToString(),
                                    TotalCost = Convert.ToDouble(factura.LineasFactura[i].ArrayPrecios[c] * factura.LineasFactura[i].ArrayUnidades[c]).ToString("N2"),//17-03-2016
                                    GrossAmount = Convert.ToDouble(factura.LineasFactura[i].ArrayPrecios[c] * factura.LineasFactura[i].ArrayUnidades[c]).ToString("N2")//17-03-2016
                                };

                                oInvoiceLine.TaxesOutputs = new FacturaElectronicaV32.InvoiceLineTypeTax[1];
                                oInvoiceLine.TaxesOutputs[0] = new FacturaElectronicaV32.InvoiceLineTypeTax();
                                oInvoiceLine.TaxesOutputs[0].TaxRate = factura.LineasFactura[i].PtjImpuesto.ToString();
                                oInvoiceLine.TaxesOutputs[0].TaxableBase = new FacturaElectronicaV32.AmountType();
                                oInvoiceLine.TaxesOutputs[0].TaxableBase.TotalAmount = Convert.ToDouble(factura.LineasFactura[i].ArrayPrecios[c] * factura.LineasFactura[i].ArrayUnidades[c]).ToString("N2");//17-03-2016

                                eFactura.Invoices[0].Items[fila] = oInvoiceLine;

                                insertoMinimoSoria = true;
                            } //Fin soria inserso

                        if (factura.LineasFactura[i].ArrayUnidades[c] != 0 && factura.LineasFactura[i].ArrayPrecios[c] != 0)
                        {

                            // Líneas de escalados como siempre
                            oInvoiceLine = new FacturaElectronicaV32.InvoiceLineType()
                            {
                                IssuerContractReference = factura.ContratoCodigo.ToString(),
                                IssuerTransactionDate = factura.Fecha.Value,
                                ReceiverContractReference = factura.ContratoCodigo.ToString(),
                                ArticleCode = tarifa.CodigoServicio.ToString() + "-" + tarifa.Codigo.ToString() + " - Consumo",
                                ItemDescription = "Consumo hasta " + factura.LineasFactura[i].ArrayEscalas[c].ToString(),
                                Quantity = Convert.ToDouble(factura.LineasFactura[i].ArrayUnidades[c]),
                                UnitOfMeasure = FacturaElectronicaV32.UnitOfMeasureType.Item33,
                                UnitOfMeasureSpecified = true,
                                UnitPriceWithoutTax = factura.LineasFactura[i].ArrayPrecios[c].ToString(),
                                TotalCost = Convert.ToDouble(factura.LineasFactura[i].ArrayPrecios[c] * factura.LineasFactura[i].ArrayUnidades[c]).ToString("N2"),//17-03-2016
                                GrossAmount = Convert.ToDouble(factura.LineasFactura[i].ArrayPrecios[c] * factura.LineasFactura[i].ArrayUnidades[c]).ToString("N2")//17-03-2016
                            };

                            oInvoiceLine.TaxesOutputs = new FacturaElectronicaV32.InvoiceLineTypeTax[1];
                            oInvoiceLine.TaxesOutputs[0] = new FacturaElectronicaV32.InvoiceLineTypeTax();
                            oInvoiceLine.TaxesOutputs[0].TaxRate = factura.LineasFactura[i].PtjImpuesto.ToString();
                            oInvoiceLine.TaxesOutputs[0].TaxableBase = new FacturaElectronicaV32.AmountType();
                            oInvoiceLine.TaxesOutputs[0].TaxableBase.TotalAmount = Convert.ToDouble(factura.LineasFactura[i].ArrayPrecios[c] * factura.LineasFactura[i].ArrayUnidades[c]).ToString("N2");//17-03-2016

                            eFactura.Invoices[0].Items[fila + 1] = oInvoiceLine;
                            fila++;
                        }
                    }
                }
                fila++;
            }

            cOficinaContableBO ofiCon = cOficinasContablesBL.Obtener(contrato.FacturaeOficinaContable, out respuesta);
            bool oficinasConExtensiones = respuesta.Resultado == ResultadoProceso.OK && ofiCon.EnviarConExtensiones.HasValue && ofiCon.EnviarConExtensiones.Value;

            if (factura.PeriodoCodigo.Substring(0, 1) != "0")
            {
                // Extensión de la factura electrónica
                FacturaElectronicaV32.UtilitiesExtension UtilitiesExtension = new FacturaElectronicaV32.UtilitiesExtension()
                {
                    Version = "1.0",
                };

                cContadorBO contadorInstalado = cCtrConBL.ObtenerUltimoContadorInstalado(factura.ContratoCodigo.Value, out respuesta);
                if (respuesta.Resultado == ResultadoProceso.Error)
                    return respuesta;
                respuesta.Resultado = ResultadoProceso.OK;

                // Datos de suministro
                FacturaElectronicaV32.DatosDelSuministroType datosSuministro = new FacturaElectronicaV32.DatosDelSuministroType();
                datosSuministro.CUPS = contadorInstalado != null ? contadorInstalado.NumSerie : String.Empty;
                datosSuministro.Contrato = new FacturaElectronicaV32.ContratoType();
                datosSuministro.Contrato.RefContratoEmpresa = factura.ContratoCodigo.ToString();
                datosSuministro.Contrato.ReferenciaPropiaCliente = contrato.TitularCodigo.ToString();
                datosSuministro.Distribuidora = sociedad.Nombre;

                cContratoBL.ObtenerInmueble(ref contrato, out respuesta);
                if (respuesta.Resultado != ResultadoProceso.OK)
                    return respuesta;

                inmueble = contrato.InmuebleBO;
                respuesta = cInmuebleBL.ObtenerPoblacion(ref inmueble);
                if (respuesta.Resultado != ResultadoProceso.OK)
                    return respuesta;

                respuesta = cInmuebleBL.ObtenerProvincia(ref inmueble);
                if (respuesta.Resultado != ResultadoProceso.OK)
                    return respuesta;

                datosSuministro.DireccionSuministro = new FacturaElectronicaV32.DireccionSuministroType();
                datosSuministro.DireccionSuministro.Direccion = contrato.InmuebleBO.Direccion;
                datosSuministro.DireccionSuministro.CodigoPostal = contrato.InmuebleBO.CodigoPostal;
                datosSuministro.DireccionSuministro.Poblacion = inmueble.Poblacion.Descripcion;
                datosSuministro.DireccionSuministro.Provincia = inmueble.Provincia.Descripcion;
                datosSuministro.DireccionSuministro.Pais = "ESP";
                datosSuministro.DireccionSuministro.RefCatastral = contrato.InmuebleBO.RefCatastral;

                eFactura.Invoices[0].AdditionalData = new FacturaElectronicaV32.AdditionalDataType();
                eFactura.Invoices[0].AdditionalData.InvoiceAdditionalInformation =
                " Dirección de suministro: " + contrato.InmuebleBO.Direccion + "\n" +
                contrato.InmuebleBO.CodigoPostal + " - " + inmueble.Poblacion.Descripcion + " - " + inmueble.Provincia.Descripcion + "\n";
                eFactura.Invoices[0].AdditionalData.InvoiceAdditionalInformation +=
                " Fecha y lectura anterior: " + (factura.FechaLecturaAnterior.HasValue ? factura.FechaLecturaAnterior.Value.ToShortDateString() + "-" : String.Empty) + factura.LecturaAnterior.ToString() + "m3\n" +
                " Fecha y lectura actual: " + (factura.FechaLecturaFactura.HasValue ? factura.FechaLecturaFactura.Value.ToShortDateString() + "-" : String.Empty) + factura.LecturaFactura.ToString() + "m3\n" +
                " Consumo: " + (factura.ConsumoFactura.HasValue ? factura.ConsumoFactura.Value : 0).ToString() + "m3\n";
                eFactura.Invoices[0].AdditionalData.InvoiceAdditionalInformation += " Total: " + (factura.TotalFacturado.HasValue ? factura.TotalFacturado.Value.ToString("N2") : "0").ToString();
                eFactura.Invoices[0].AdditionalData.InvoiceAdditionalInformation += incidencia.MCalculo == "E" || incidencia.MCalculo == "EP" ? "Estimada\n" : String.Empty;

                UtilitiesExtension.DatosDelSuministro = datosSuministro;

                if (oficinasConExtensiones)
                {
                    short aguaCodigo;
                    respuesta = cParametroBL.GetShort("SERVICIO_AGUA", out aguaCodigo);
                    if (respuesta.Resultado != ResultadoProceso.OK)
                        return respuesta;

                    cLineaFacturaBO lineaAgua = new cLineaFacturaBO();
                    lineaAgua.CodigoServicio = aguaCodigo;
                    lineaAgua.Contrato = factura.ContratoCodigo.HasValue ? factura.ContratoCodigo.Value : 0;
                    lineaAgua.Periodo = factura.PeriodoCodigo;
                    lineaAgua.Version = factura.Version.HasValue ? factura.Version.Value : (short)0;
                    lineaAgua.FacturaCodigo = factura.FacturaCodigo.HasValue ? factura.FacturaCodigo.Value : (short)0;

                    new cLineasFacturaBL().Obtener(ref lineaAgua, out respuesta);

                    if (respuesta.Resultado != ResultadoProceso.OK)
                    {
                        if (respuesta.Resultado == ResultadoProceso.Error)
                            return respuesta;
                        else
                            respuesta.Resultado = ResultadoProceso.OK;
                    }
                    else
                    {
                        cTarvalBO tarVal = new cTarvalBO();
                        tarVal.Codigo = lineaAgua.CodigoTarifa;
                        tarVal.CodigoServicio = lineaAgua.CodigoServicio;
                        cTarvalBL.Obtener(ref tarVal, out respuesta);

                        if (respuesta.Resultado == ResultadoProceso.Error)
                            return respuesta;
                        respuesta.Resultado = ResultadoProceso.OK;
                        if (respuesta.Resultado == ResultadoProceso.OK)
                        {
                            datosSuministro.ReferenciaLegal = new FacturaElectronicaV32.ReferenciaLegalType();
                            datosSuministro.ReferenciaLegal.BOEBOCA = tarVal.LegalAvb;
                        }

                        if (contrato != null && contrato.UsoCodigo.HasValue)
                        {
                            datosSuministro.Usos = new FacturaElectronicaV32.UsosType();
                            if (contrato.UsoCodigo == 1) // Doméstico = 1
                                datosSuministro.Usos.NumeroViviendas = Convert.ToInt32(lineaAgua.Unidades).ToString();
                            else
                                datosSuministro.Usos.NumeroLocales = Convert.ToInt32(lineaAgua.Unidades).ToString();
                        }
                    }

                    // Si la factura es domiciliada
                    if (!String.IsNullOrEmpty(contrato.Iban) && !String.IsNullOrEmpty(contrato.Bic))
                    {
                        cBicBO bic = cBicBL.Obtener(contrato.Bic, out respuesta);
                        if (respuesta.Resultado != ResultadoProceso.OK)
                            return respuesta;
                        datosSuministro.NombreBanco = bic.Nombre;
                        datosSuministro.TitularBancario = !String.IsNullOrEmpty(contrato.PagadorDocIden) ? contrato.TitularNombre : contrato.PagadorNombre;
                    }

                    datosSuministro.OrigenFactura = "ES";
                    datosSuministro.IDDocumento = codigoExplotacion + "-" + factura.SerieCodigo.ToString() + factura.Numero.ToString();
                    datosSuministro.TotalAPagar = Convert.ToDouble(factura.TotalFacturado.HasValue ? factura.TotalFacturado.Value : 0);

                    // Medidas
                    FacturaElectronicaV32.UtilitiesMedidaType medidas = new FacturaElectronicaV32.UtilitiesMedidaType();
                    medidas.MedidasSobreEquipo = new FacturaElectronicaV32.MedidaSobreEquipoType[1];
                    medidas.MedidasSobreEquipo[0] = new FacturaElectronicaV32.MedidaSobreEquipoType();
                    medidas.MedidasSobreEquipo[0].Calibre = contadorInstalado.Diametro.ToString();
                    medidas.MedidasSobreEquipo[0].LecturaDesdeSpecified = true;
                    medidas.MedidasSobreEquipo[0].LecturaDesde = factura.LecturaAnterior;
                    medidas.MedidasSobreEquipo[0].LecturaHastaSpecified = true;
                    medidas.MedidasSobreEquipo[0].LecturaHasta = factura.LecturaFactura;

                    incidencia.Codigo = String.IsNullOrEmpty(factura.InspectorIncidenciaLectura) ? (String.IsNullOrEmpty(factura.LectorIncidenciaLectura) ? null : factura.LectorIncidenciaLectura) : factura.InspectorIncidenciaLectura;

                    if (!String.IsNullOrEmpty(incidencia.Codigo))
                    {
                        new cIncilecBL().Obtener(ref incidencia, out respuesta);
                        if (respuesta.Resultado != ResultadoProceso.OK)
                            return respuesta;
                    }

                    medidas.MedidasSobreEquipo[0].TipoDeLecturaActual = incidencia != null && (incidencia.MCalculo == "E" || incidencia.MCalculo == "EP") ? "Estimado" : "Leido";
                    if (medidas.MedidasSobreEquipo[0].TipoDeLecturaActual != "Estimada")
                    {
                        medidas.MedidasSobreEquipo[0].ConsumoLeidoSpecified = true;
                        medidas.MedidasSobreEquipo[0].ConsumoLeido = factura.ConsumoFactura.HasValue ? factura.ConsumoFactura.Value : 0;
                    }
                    else
                    {
                        medidas.MedidasSobreEquipo[0].ConsumoCalculadoSpecified = true;
                        medidas.MedidasSobreEquipo[0].ConsumoCalculado = factura.ConsumoFactura.HasValue ? factura.ConsumoFactura.Value : 0;
                    }

                    cFacturasBL.ObtenerPeriodo(ref factura, out respuesta);
                    if (respuesta.Resultado != ResultadoProceso.OK)
                        return respuesta;

                    // Histórico de consumos
                    FacturaElectronicaV32.HistoricoConsumoType[] historicos = new FacturaElectronicaV32.HistoricoConsumoType[2];
                    historicos[0] = new FacturaElectronicaV32.HistoricoConsumoType();
                    historicos[0].Periodo = factura.PeriodoCodigo;
                    historicos[0].Descripcion = factura.Periodo.Descripcion;
                    historicos[0].ValorSpecified = true;
                    historicos[0].Valor = factura.ConsumoFactura.HasValue ? factura.ConsumoFactura.Value : 0;
                    historicos[0].UnidadMedida = "m3";
                    historicos[0].FechaIniPeriodoSpecified = true;
                    historicos[0].FechaIniPeriodo = factura.FechaLecturaAnterior;
                    historicos[0].FechaFinPeriodoSpecified = true;
                    historicos[0].FechaFinPeriodo = factura.FechaLecturaFactura;
                    historicos[0].TipoCalculo = "Exacto";

                    cFacturaBO facturaAnterior = new cFacturaBO();
                    facturaAnterior.PeriodoCodigo = new cPeriodoBL().ObtenerPeriodoConsumoAnterior(factura.PeriodoCodigo, out respuesta);
                    facturaAnterior.ContratoCodigo = factura.ContratoCodigo;

                    if (respuesta.Resultado == ResultadoProceso.Error)
                        return respuesta;

                    if (respuesta.Resultado == ResultadoProceso.OK)
                    {
                        cFacturasBL.Obtener(ref facturaAnterior, out respuesta);

                        if (respuesta.Resultado != ResultadoProceso.OK)
                            return respuesta;

                        historicos[1] = new FacturaElectronicaV32.HistoricoConsumoType();
                        historicos[1].Periodo = facturaAnterior.PeriodoCodigo;
                        historicos[1].Descripcion = facturaAnterior.Periodo.Descripcion;
                        historicos[1].ValorSpecified = true;
                        historicos[1].Valor = facturaAnterior.ConsumoFactura.HasValue ? facturaAnterior.ConsumoFactura.Value : 0;
                        historicos[1].UnidadMedida = "m3";
                        historicos[1].FechaIniPeriodoSpecified = true;
                        historicos[1].FechaIniPeriodo = facturaAnterior.FechaLecturaAnterior;
                        historicos[1].FechaFinPeriodoSpecified = true;
                        historicos[1].FechaFinPeriodo = facturaAnterior.FechaLecturaFactura;
                        historicos[1].TipoCalculo = "Exacto";
                    }

                    respuesta.Resultado = ResultadoProceso.OK;

                    // Datos adicionales
                    string[] datosAdicionales = new string[1];
                    string ibanOculto = "INGRESO EN CUENTA DE " + nombreExplotacion;
                    if (!String.IsNullOrEmpty(contrato.Iban))
                    {
                        if (contrato.Iban.Length > 34 || contrato.Iban.Length < 24)
                            ibanOculto = "INGRESO EN CUENTA DE " + nombreExplotacion;
                        else
                            ibanOculto = "Será cargada en: " + contrato.Iban.Substring(0, 12) + "********" + contrato.Iban.Substring(20);
                    }

                    datosAdicionales[0] = ibanOculto + " ";
                   
                    UtilitiesExtension.UtilitiesMedida = medidas;
                    UtilitiesExtension.UtilitiesHistoricoConsumos = historicos;
                    UtilitiesExtension.DatosPagoAdicionales = datosAdicionales;

                    XmlDocument xmld2 = new XmlDocument();

                    //XmlSerializerNamespaces namespaces = new XmlSerializerNamespaces();
                    //namespaces.Add("ex", "http://www.facturae.es/Facturae/Extensions/Utilities");

                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (XmlTextWriter xmltw = new XmlTextWriter(ms, Encoding.UTF8))
                        {
                            XmlSerializer xmlser = new XmlSerializer(typeof(FacturaElectronicaV32.UtilitiesExtension));
                            //xmlser.Serialize(xmltw, UtilitiesExtension, namespaces);
                            xmlser.Serialize(xmltw, UtilitiesExtension);
                            ms.Seek(0, SeekOrigin.Begin);
                            xmld2.Load(ms);
                        }
                    }
                    eFactura.Invoices[0].AdditionalData.Extensions = new FacturaElectronicaV32.ExtensionsType();
                    eFactura.Invoices[0].AdditionalData.Extensions.Any = new XmlElement[] { xmld2.DocumentElement };
                }
            }

            // Actualizar el campo referente al momento en el que se encuentra el proceso SERES
            if (respuesta.Resultado == ResultadoProceso.OK)
            {
                factura.EnvSERES = "E";
                string log = String.Empty;

                cFacturasBL.Actualizar(factura, false, out log, out respuesta);
            }

            if (respuesta.Resultado == ResultadoProceso.OK)
                facturaXML = Serializar(eFactura);

            return respuesta;
        }

        public static XmlDocument Serializar(FacturaElectronicaV321.Facturae eFactura)
        {

            XmlSerializerNamespaces namespaces = new XmlSerializerNamespaces();
            namespaces.Add("xsi", "http://www.w3.org/2001/XMLSchema-instance");
            namespaces.Add("fe", "http://www.facturae.es/Facturae/2014/v3.2.1/Facturae");
            namespaces.Add("ex", "http://www.facturae.es/Facturae/Extensions/Utilities");
            //namespaces.Add("ds", "http://www.w3.org/2000/09/xmldsig");

            using (MemoryStream ms = new MemoryStream())
            {
                using (XmlTextWriter xmltw = new XmlTextWriter(ms, Encoding.UTF8))
                {
                    XmlSerializer xmlser = new XmlSerializer(typeof(FacturaElectronicaV321.Facturae));
                    xmlser.Serialize(xmltw, eFactura, namespaces);

                    ms.Seek(0, SeekOrigin.Begin);
                    XmlDocument xmld = new XmlDocument();
                    xmld.Load(ms);
                    return xmld;
                }
            }
        }

        public static XmlDocument Serializar(FacturaElectronicaV32.Facturae eFactura)
        {

            XmlSerializerNamespaces namespaces = new XmlSerializerNamespaces();
            namespaces.Add("xsi", "http://www.w3.org/2001/XMLSchema-instance");
            namespaces.Add("fe", "http://www.facturae.es/Facturae/2009/v3.2/Facturae");
            namespaces.Add("ex", "http://www.facturae.es/Facturae/Extensions/Utilities");
            //namespaces.Add("ds", "http://www.w3.org/2000/09/xmldsig");

            using (MemoryStream ms = new MemoryStream())
            {
                using (XmlTextWriter xmltw = new XmlTextWriter(ms, Encoding.UTF8))
                {
                    XmlSerializer xmlser = new XmlSerializer(typeof(FacturaElectronicaV32.Facturae));
                    xmlser.Serialize(xmltw, eFactura, namespaces);

                    ms.Seek(0, SeekOrigin.Begin);
                    XmlDocument xmld = new XmlDocument();
                    xmld.Load(ms);
                    return xmld;
                }
            }
        }

        #region RevisionImportesFacE
        private static bool omitirFacE(cFacturaBO factura, cBindableList<RevisionImportesFaceBO> facturasKO, ref string taskLog)
        {
            bool omitir = false;
            RevisionImportesFaceBO facturaKO = null;

            try
            {
                facturaKO = facturasKO.FirstOrDefault(x => x.facCod == factura.FacturaCodigo
                                    && x.facCtrCod == factura.ContratoCodigo
                                    && x.facPerCod == factura.PeriodoCodigo
                                    && x.facVersion == factura.Version);
                if (facturaKO != null)
                {
                    taskLog = string.IsNullOrEmpty(taskLog) ? string.Format(Resource.RevisionImportesFacE, Environment.NewLine) : taskLog;
                    taskLog += facturaKO.Log;
                    omitir = true;
                }
            }
            catch
            {

            }
            return omitir;
        }
      
        #endregion RevisionImportesFacE

        #region  Face rectificativa de no FacE

        private static cBindableList<cFacturaBO> ObtenerRectificadasNoEnviadasFacE(cBindableList<cFacturaBO> facturas, bool ctrFace)
        {
            //Buscamos las facturas rectificadas que no se han enviado a face
            cBindableList<cFacturaBO> result = new cBindableList<cFacturaBO>();

            try
            {
                cRespuesta respuestaAdd = new cRespuesta();
                result = cFacturasBL.ObtenerRectificadasNoEnviadasFacE(facturas, ctrFace, out respuestaAdd);
            }
            catch
            {

            }

            return result;
        }

        private static cBindableList<cFacturaBO> InsertarRectificadasNoEnviadasFacE(ref cBindableList<cFacturaBO> facturas)
        {
            cBindableList<cFacturaBO> result = new cBindableList<cFacturaBO>();
            cRespuesta respuesta = new cRespuesta();
            try
            {
                cBindableList<cFacturaBO> lstRectificadas;
                lstRectificadas = ObtenerRectificadasNoEnviadasFacE(facturas, true);

                for (int i = 0; i < lstRectificadas.Count; i++)
                {
                    //Comprobamos antes que no esté ya en el listado original de facturas
                    cFacturaBO objFactura;
                    cFacturaBO facRectif = lstRectificadas[i];

                    objFactura = facturas.FirstOrDefault(f => f.FacturaCodigo   == facRectif.FacturaCodigo
                                                           && f.ContratoCodigo  == facRectif.ContratoCodigo
                                                           && f.PeriodoCodigo   == facRectif.PeriodoCodigo
                                                           && f.Version         == facRectif.Version);
                    if (objFactura == null)
                    {
                        facturas.Add(facRectif);
                        result.Add(facRectif);
                    }
                }

            }
            catch
            {

            }
            return result;
        }

        private static cBindableList<cFacturaBO> ExcluirRectificadasNoEnviadasFacE(ref cBindableList<cFacturaBO> facturas)
        {
            cBindableList<cFacturaBO> result = new cBindableList<cFacturaBO>();
            cRespuesta respuesta = new cRespuesta();
            try
            {
                cBindableList<cFacturaBO> lstRectificadas;
                lstRectificadas = ObtenerRectificadasNoEnviadasFacE(facturas, false);

                for (int i = 0; i < lstRectificadas.Count; i++)
                {
                    //Si hay rectificadas no enviadas pero que no tienen marcado ctrFace omitimos el envio de esa factura  
                    cBindableList<cFacturaBO> lstFacturas;
                    cFacturaBO facRectif = lstRectificadas[i];

                    lstFacturas = facturas.Where(f => f.FacturaCodigo == facRectif.FacturaCodigo
                                                    && f.ContratoCodigo == facRectif.ContratoCodigo
                                                    && f.PeriodoCodigo == facRectif.PeriodoCodigo).ConvertTocBlindableList<cFacturaBO>();
                    foreach (cFacturaBO fac in lstFacturas)
                    {
                        facturas.Remove(fac);
                        result.Add(fac);
                    }
                }

            }
            catch
            {

            }
            return result;
        }

        private static string ProcesarRectificativasNoEnviadasFacE(ref cBindableList<cFacturaBO> facturas)
        {
            string result = string.Empty;
            int facturasCount = facturas.Count;

            //Si la factura que se va a enviar es una rectificativa es necesario enviar a FacE las versiones anteriores.
            //Si las versiones anteriores no tienen marcado ctrFace=1 las omitimos del envio =>
            //El usuario tendrá que hacer la actualización de version del contrato y hacer de nuevo la emisión facE
            cBindableList<cFacturaBO> excluidas = ExcluirRectificadasNoEnviadasFacE(ref facturas);

            //Excluidas las facturas que no tienen todas las rectificadas con ctrFace=1
            //Podemos ahora incluir las rectificadas que si están pendientes de enviar a facE
            cBindableList<cFacturaBO> incluidas = InsertarRectificadasNoEnviadasFacE(ref facturas);

            //Enviamos información para el log con las facturas que no se van a poder emitir mientras no tengan ctrFacE=1 en todas las facturas pendientes de envio
            if(excluidas.Count > 0)
            {
                result = string.Join(Environment.NewLine,
                                    excluidas.Select(x => string.Format(Resource.RectificativasNoEnviadasFacE_Log
                                                                      , x.FacturaCodigo, x.PeriodoCodigo, x.ContratoCodigo, x.Version)));
                
                result = string.Format(Resource.ProcesarRectificativasNoEnviadasFacE, Environment.NewLine) + result + Environment.NewLine;

                result += string.Format(Resource.ProcesarRectificativasNoEnviadasFacE_Log, facturasCount, excluidas.Count, incluidas.Count) + Environment.NewLine; 
            }
            return result;
        }

        #endregion Face rectificativa de no FacE

        #region autoAjusteLineas_FacE
        private static StringBuilder autoAjusteLineas_FacE(cBindableList<cFacturaBO> facturas)
        {
            List<string> logAjusteLineas = new List<string>();

            bool necesitaAjuste = false;

            ImportesFacturaBO totalSeres = null;
            ImportesFacturaBO totalAcuama = null;
            ImportesFacturaBO totalAjustado = null;

            IList<cAjusteLineasFacE> facLin_Ajustar = new List<cAjusteLineasFacE>();

            CultureInfo ES = CultureInfo.GetCultureInfo("es-ES");
            StringBuilder strResult = new StringBuilder();
            string rFacturaLog_ = Resource.RevisionImportesFacE_Factura_;
            string rFacturaLogH = Resource.RevisionImportesFacE_FacturaH;

            string rLineaLog_ = Resource.RevisionImportesFacE_Linea_;
            string rLineaLogH = Resource.RevisionImportesFacE_LineaH;

            try
            {
                rFacturaLogH = string.Format(rFacturaLogH, rLineaLogH) + Environment.NewLine;

                foreach (cFacturaBO fac in facturas)
                {
                    necesitaAjuste = necesitaAjusteFacE(fac, out totalAcuama, out totalSeres);

                    if (necesitaAjuste)
                    {
                        facLin_Ajustar = lineasParaAjustar(fac, totalSeres);

                        if (facLin_Ajustar.Count > 0)
                            logAjusteLineas = aplicarAjusteLineasFacE(fac, facLin_Ajustar, out totalAjustado);


                        if (logAjusteLineas.Count > 0)
                        {
                            logAjusteLineas.ForEach(x => strResult.AppendLine(
                                string.Format(ES, rFacturaLog_
                                            , fac.FacturaCodigo, fac.PeriodoCodigo, fac.ContratoCodigo, fac.Version
                                            , totalAcuama.TOTAL_FACTURA, totalAjustado.TOTAL_FACTURA, totalSeres.TOTAL_FACTURA
                                            , totalAcuama.TOTAL_IMP_REP, totalAjustado.TOTAL_IMP_REP, totalSeres.TOTAL_IMP_REP
                                            , x)));
                        }
                    }
                }
            }
            catch
            {

            }
            finally
            {
                if (strResult.Length > 0)
                    strResult = strResult.Insert(0, rFacturaLogH);
            }

            return strResult;
        }

        private static bool necesitaAjusteFacE(cFacturaBO factura, out ImportesFacturaBO totalAcuama, out ImportesFacturaBO totalSeres)
        {
            cRespuesta respuesta = new cRespuesta(ResultadoProceso.Error);
            bool result = false;

            IList<cLineaFacturaBO> lineasConImpuesto = null;
            cLineasFacturaBL objLineasBL = new cLineasFacturaBL();

            totalAcuama = null;
            totalSeres = null;
            bool facturaPdte = false;

            try
            {
                //Antes de mirar nada, comprobamos que la factura este pendiente de cobro
                facturaPdte = esFacturaConDeuda(factura);
                if (!facturaPdte) return false;

                cFacturasBL.ObtenerLineas(ref factura, out respuesta);

                if (!respuesta.EsResultadoCorrecto) return result;

                //Nos interesan solo las lineas con impuesto
                lineasConImpuesto = factura.LineasFactura.Where(x => x.PtjImpuesto != 0).ToList();
                if (lineasConImpuesto == null || lineasConImpuesto.Count == 0) return result;

                totalAcuama = facTotalAcuama(factura, out respuesta);
                if (!respuesta.EsResultadoCorrecto) return result;

                totalSeres = facTotalSeres(factura, out respuesta);
                if (!respuesta.EsResultadoCorrecto) return result;

                result = (totalSeres.TOTAL_IMP_REP != totalAcuama.TOTAL_IMP_REP) || 
                         (totalSeres.TOTAL_FACTURA != totalAcuama.TOTAL_FACTURA) || 
                         (totalAcuama.TOTAL_FACTURA != totalAcuama.TOTAL_BASEIMPO+totalAcuama.TOTAL_IMP_REP);
            }
            catch
            {
                result = false;
            }
            finally
            {

            }

            return result;
        }

        private static bool esFacturaConDeuda(cFacturaBO factura)
        {
            bool result = false;
            cRespuesta respuesta = new cRespuesta(ResultadoProceso.Error);

            try
            {
                //Miramos si la ultima version de esta factura esta pendiente de pago
                cBindableList<cFacturaBO> facPdtes = cFacturasBL.ObtenerPorTipoDeuda(factura.ContratoCodigo ?? 0, 0, null, cFacturasDL.TipoDeuda.PendienteCobro, out respuesta);

                result = facPdtes.Any(x => x.FacturaCodigo == factura.FacturaCodigo && x.PeriodoCodigo == factura.PeriodoCodigo && x.ContratoCodigo == factura.ContratoCodigo &&  x.FacturaRectificada == null);
            }
            catch
            {

            }

            return result;
        }

        private static ImportesFacturaBO facTotalAcuama(cFacturaBO factura, out cRespuesta respuesta)
        {
            ImportesFacturaBO result = new ImportesFacturaBO();
            cLineasFacturaBL objLineasBL = new cLineasFacturaBL();
            respuesta = new cRespuesta(ResultadoProceso.Error);

            try
            {
                //En Acuama: hay una linea de impuesto por linea de factura, cada una con su impuesto
                result.TOTAL_IMP_REP = Math.Round(factura.LineasFactura.Sum(l => l.ImpImpuesto), 2, MidpointRounding.AwayFromZero);
                result.TOTAL_BASEIMPO = factura.LineasFactura.Sum(l => l.CBase);
                result.TOTAL_FACTURA = Math.Round(factura.LineasFactura.Sum(l => l.Total), 2, MidpointRounding.AwayFromZero);

                respuesta = new cRespuesta(ResultadoProceso.OK);
            }
            catch
            {

            }
            finally
            {

            }

            return result;
        }

        private static ImportesFacturaBO facTotalSeres(cFacturaBO factura, out cRespuesta respuesta)
        {
            ImportesFacturaBO result = new ImportesFacturaBO();
            cLineasFacturaBL objLineasBL = new cLineasFacturaBL();
            respuesta = new cRespuesta(ResultadoProceso.Error);

            try
            {
                //En Seres: Una linea de base por escala, una linea de base por cuota todo a 2 decimales, para ello usamos TotalizarBloques
                //Una linea de impuesto por tipo impositivo, por lo que tenemos antes que totalizar las bases por tipo impositivo
                var BasesxTipo = from lin in factura.LineasFactura
                                 group lin by lin.PtjImpuesto into g
                                 select new
                                 {
                                     tipoImp = g.Key,
                                     totalBase = g.Sum(x => objLineasBL.TotalizarBloques(x, true))
                                 };

                if (BasesxTipo != null)
                {
                    result.TOTAL_IMP_REP = BasesxTipo.Sum(x => Math.Round(x.totalBase * x.tipoImp * 0.01M, 2, MidpointRounding.AwayFromZero));
                    result.TOTAL_BASEIMPO = BasesxTipo.Sum(x => x.totalBase);
                    result.TOTAL_FACTURA = result.TOTAL_IMP_REP + result.TOTAL_BASEIMPO;
                    respuesta = new cRespuesta(ResultadoProceso.OK);
                }
            }
            catch
            {

            }
            finally
            {

            }

            return result;
        }

        private static IList<cAjusteLineasFacE> lineasParaAjustar(cFacturaBO factura, ImportesFacturaBO Total_Seres)
        {
            cRespuesta respuesta = new cRespuesta(ResultadoProceso.Error);
            IList<cAjusteLineasFacE> result = new List<cAjusteLineasFacE>();

            IList<cLineaFacturaBO> lineas = factura.LineasFactura;

            cLineasFacturaBL objLineasBL = new cLineasFacturaBL();
            decimal TotalImpRep_Ajustado = 0;
            decimal Total_Ajustado = 0;

            cAjusteLineasFacE ajuste = null;

            try
            {
                //*****************************************************
                //Es posible que la precisión o el valor de las lineas no sea el correcto.
                //Recalculamos el total de la base de cada linea como seres: por escalados a 2 decimales
                var facLinSeres = from x in lineas
                                  select new cAjusteLineasFacE
                                  {
                                      NumeroLinea = x.NumeroLinea,
                                      Impuesto = x.PtjImpuesto,
                                      cBase = objLineasBL.TotalizarBloques(x, true)
                                  };


                //Con la nueva base recalculamos los impuestos con 4 decimales Base*tipoImpositivo
                var facLin4Dec = from x in facLinSeres
                                 select new cAjusteLineasFacE
                                 {
                                     NumeroLinea = x.NumeroLinea,
                                     cBase = x.cBase,
                                     Impuesto = x.Impuesto,

                                     ImpImpuesto = Math.Round(x.cBase * x.Impuesto * 0.01M, 4, MidpointRounding.AwayFromZero)
                                 };

                //Comprobamos si así coincide con seres, en cuyo caso mandamos todas las lineas a ajustar porque ha sido culpa de la precisión en el impuesto
                TotalImpRep_Ajustado = Math.Round(facLin4Dec.Sum(l => l.ImpImpuesto), 2, MidpointRounding.AwayFromZero);
                Total_Ajustado = Math.Round(facLin4Dec.Sum(l => l.ImpImpuesto + l.cBase), 2, MidpointRounding.AwayFromZero);

                if (TotalImpRep_Ajustado == Total_Seres.TOTAL_IMP_REP && Total_Ajustado == Total_Seres.TOTAL_FACTURA)
                {
                    //Basta con dejar los impuestos a 4 decimales y las bases a 2 para conseguir el ajuste en ambas facturas
                    return facLin4Dec.ToList();
                }

                //*****************************************************
                //Si no ha sido posible ajustar cambiando la precision:
                //Buscamos la primera linea que al redondear a dos decimales el impuesto permita que ambos totales coincidan
                foreach (cAjusteLineasFacE lin in facLin4Dec)
                {
                    //Se redondea el impuesto a 2 decimales la linea y vemos si con eso conseguimos que los totales de acuama y seres coincidan
                    var lineasAjuste = from x in facLin4Dec
                                       select new cAjusteLineasFacE
                                       {
                                           NumeroLinea = x.NumeroLinea,
                                           Impuesto = x.Impuesto,
                                           ImpImpuesto = x.NumeroLinea == lin.NumeroLinea ? Math.Round(x.ImpImpuesto, 2, MidpointRounding.AwayFromZero) : x.ImpImpuesto,
                                           cBase = x.cBase
                                       };

                    TotalImpRep_Ajustado = Math.Round(lineasAjuste.Sum(l => l.ImpImpuesto), 2, MidpointRounding.AwayFromZero);
                    Total_Ajustado = Math.Round(lineasAjuste.Sum(l => l.ImpImpuesto + l.cBase), 2, MidpointRounding.AwayFromZero);

                    if (TotalImpRep_Ajustado == Total_Seres.TOTAL_IMP_REP && Total_Ajustado == Total_Seres.TOTAL_FACTURA)
                    {
                        ajuste = facLin4Dec.Where(x => x.NumeroLinea == lin.NumeroLinea).FirstOrDefault();
                        ajuste.ImpImpuesto = Math.Round(ajuste.ImpImpuesto, 2, MidpointRounding.AwayFromZero);
                        break;
                    }
                }

                //*****************************************************
                //Si encontramos una linea que dejaría la factura ajustada: enviamos las lineas a 4 decimales y la ajustada (2decimales)
                if (ajuste != null)
                {
                    //Enviamos a ajustar las lineas que deberían estar a 4 decimales (omitimos la que corresponde al ajuste)
                    var faclinAjustes = from linea in lineas
                                        join linea4 in facLin4Dec
                                        on linea.NumeroLinea equals linea4.NumeroLinea
                                        where linea.NumeroLinea != ajuste.NumeroLinea &&
                                        (linea.ImpImpuesto != linea4.ImpImpuesto || linea.CBase != linea4.cBase || linea.Total != linea4.Total) 
                                        select new cAjusteLineasFacE
                                        {
                                            NumeroLinea = linea.NumeroLinea,
                                            Impuesto = linea.PtjImpuesto,
                                            ImpImpuesto = linea4.ImpImpuesto, 
                                            cBase = linea4.cBase
                                        };
                    //Insertamos la linea que corresponde al ajuste
                    result = faclinAjustes.ToList();
                    result.Add(ajuste);
                }
            }
            catch
            {

            }
            finally
            {

            }
            return result;
        }

        private static List<string> aplicarAjusteLineasFacE(cFacturaBO factura, IList<cAjusteLineasFacE> facLin_Ajustar, out ImportesFacturaBO impuestosTotal_Ajustado)
        {
            cRespuesta respuesta = new cRespuesta(ResultadoProceso.Error);
            bool resultado = false;

            List<string> result = new List<string>();
            string lineaLog = string.Empty;

            impuestosTotal_Ajustado = null;

            cLineasFacturaDL objLineasDL = new cLineasFacturaDL();
            CultureInfo ES = CultureInfo.GetCultureInfo("es-ES");

            try
            {
                string rLineaLog = Resource.RevisionImportesFacE_Linea_;

                using (TransactionScope scope = cAplicacion.NewTransactionScope())
                {
                    foreach (cLineaFacturaBO fcl in factura.LineasFactura)
                    {
                        cAjusteLineasFacE ajuste = facLin_Ajustar.Where(x => x.NumeroLinea == fcl.NumeroLinea &&  
                                                                            (x.ImpImpuesto != fcl.ImpImpuesto || x.cBase != fcl.CBase || x.Total != fcl.Total)).FirstOrDefault();

                        if (ajuste == null) continue;

                     
                        //Log antes de materializar el update
                        lineaLog = string.Format(ES, rLineaLog
                            , fcl.NumeroLinea
                            , fcl.Total
                            , ajuste.Total
                            , fcl.PtjImpuesto
                            , fcl.ImpImpuesto
                            , ajuste.ImpImpuesto
                            , fcl.CBase
                            , ajuste.cBase);

                        fcl.ImpImpuesto = ajuste.ImpImpuesto;
                        fcl.CBase = ajuste.cBase;
                        fcl.Total = ajuste.Total;

                        resultado = objLineasDL.Actualizar(fcl, out respuesta);
                        if (!resultado) return new List<string>();

                        result.Add(lineaLog);
                        cErrorLogBL.Insertar("BL.Facturacion.cEFacturasBL.aplicarAjusteLineasFacE", lineaLog, factura.facLog + "/ " + Resource.RevisionImportesFacE_LineaH, out respuesta, false);
                    }

                    impuestosTotal_Ajustado = facTotalAcuama(factura, out respuesta);
                    if (!resultado) return new List<string>();

                    scope.Complete();
                }

            }
            catch
            {
                result = new List<string>();
            }
            finally
            {

            }

            return result;
        }

        #endregion autoAjusteLineas_FacE




    }
}
